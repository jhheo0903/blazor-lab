using DeployTool.Models;
using System.Diagnostics;
using System.IO.Compression;

namespace DeployTool.Services;

public class BackupService(ILogger<BackupService> logger)
{
    // 동시 복사 수 상한 — 디스크 포화와 스레드 풀 고갈을 막는다
    private static readonly int MaxParallelism = Math.Min(Environment.ProcessorCount * 2, 16);

    private const int ProgressIntervalMs = 200;

    // ZIP 항목의 수정 시각은 DOS 형식(2초 단위)으로 저장된다
    private static readonly TimeSpan ZipTimestampTolerance = TimeSpan.FromSeconds(2);

    private sealed record CopyJob(string Source, string Destination);

    /// <summary>
    /// 선택 범위를 병렬 복사한 뒤 ZIP으로 압축하고, 압축 파일 경로를 반환합니다.
    /// 압축에 실패하면 복사본 폴더를 그대로 두고 폴더 경로를 반환합니다.
    /// </summary>
    public Task<string> CreateBackupAsync(
        string productionPath,
        ScopeSelection scope,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 동기 파일 I/O가 Blazor 회로 컨텍스트를 점유하지 않도록 스레드 풀에서 실행
        return Task.Run(
            () => CreateBackupCoreAsync(productionPath, scope, progress, cancellationToken),
            cancellationToken);
    }

    private async Task<string> CreateBackupCoreAsync(
        string productionPath,
        ScopeSelection scope,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        // Backup 폴더 전체를 복사에서 제외 (무한 재귀 방지)
        var backupParent = Path.Combine(productionPath, "Backup");
        var backupRoot = Path.Combine(backupParent, $"backup_{timestamp}");
        Directory.CreateDirectory(backupRoot);

        var jobs = new List<CopyJob>();
        if (scope.Mode == ScopeMode.FullCompare)
        {
            CollectCopyJobs(productionPath, backupRoot, backupParent, scope.ExcludePatterns, jobs);
        }
        else
        {
            var selectedPaths = scope.FolderTree
                .SelectMany(n => n.Flatten())
                .Where(n => n.IsSelected && (n.Children.Count == 0 || n.Children.All(c => !c.IsSelected)))
                .Select(n => n.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var relPath in selectedPaths)
            {
                var srcFolder = Path.Combine(productionPath, relPath);
                if (Directory.Exists(srcFolder))
                    CollectCopyJobs(srcFolder, Path.Combine(backupRoot, relPath), backupParent, scope.ExcludePatterns, jobs);
            }
        }

        await CopyFilesAsync(jobs, "백업", progress, cancellationToken);
        var copyElapsed = sw.ElapsedMilliseconds;

        progress?.Report($"압축 중... ({jobs.Count}개 파일)");
        var zipPath = backupRoot + ".zip";
        try
        {
            await ZipFile.CreateFromDirectoryAsync(
                backupRoot, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false, cancellationToken);
            Directory.Delete(backupRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 복사본은 이미 완성됐으므로 압축 실패로 백업 전체를 실패 처리하지 않는다
            logger.LogWarning(ex, "백업 압축 실패, 폴더 백업을 유지합니다: {BackupRoot}", backupRoot);
            TryDeleteFile(zipPath);
            return backupRoot;
        }

        logger.LogInformation(
            "백업 완료: {BackupPath} ({Count}개 파일, 복사 {CopyMs}ms, 전체 {TotalMs}ms)",
            zipPath, jobs.Count, copyElapsed, sw.ElapsedMilliseconds);
        return zipPath;
    }

    /// <summary>
    /// 백업(ZIP 또는 폴더)을 운영 경로로 복원합니다.
    /// </summary>
    public Task RestoreBackupAsync(
        string backupPath, string productionPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            if (backupPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await RestoreFromZipAsync(backupPath, productionPath, progress, cancellationToken);
            }
            else
            {
                var jobs = new List<CopyJob>();
                CollectCopyJobs(backupPath, productionPath, null, [], jobs);
                await CopyFilesAsync(jobs, "복원", progress, cancellationToken);
            }

            logger.LogInformation("롤백 완료: {BackupPath} -> {ProductionPath}", backupPath, productionPath);
        }, cancellationToken);
    }

    private async Task RestoreFromZipAsync(
        string zipPath, string productionPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var rootFullPath = Path.GetFullPath(productionPath);
        if (!rootFullPath.EndsWith(Path.DirectorySeparatorChar))
            rootFullPath += Path.DirectorySeparatorChar;

        await using var archive = await ZipFile.OpenReadAsync(zipPath, cancellationToken);
        var restored = 0;
        var unchanged = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 디렉터리 항목
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var destination = Path.GetFullPath(Path.Combine(productionPath, entry.FullName));
            if (!destination.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"백업 파일에 운영 경로 밖을 가리키는 항목이 있습니다: {entry.FullName}");

            // 백업 이후 바뀌지 않은 파일은 덮어쓰지 않는다.
            // (ZIP 시각은 2초 단위라 덮어쓰면 수정 시각이 미세하게 바뀌어 다음 스캔에서 '변경'으로 잡힌다)
            if (IsUnchangedSinceBackup(destination, entry))
            {
                unchanged++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await entry.ExtractToFileAsync(destination, overwrite: true, cancellationToken);
            restored++;
            progress?.Report($"복원 중 {restored}개 — {entry.Name}");
        }

        logger.LogInformation("ZIP 복원: 복원 {Restored}개, 변경 없음 {Unchanged}개", restored, unchanged);
    }

    private static bool IsUnchangedSinceBackup(string destination, ZipArchiveEntry entry)
    {
        var info = new FileInfo(destination);
        return info.Exists
            && info.Length == entry.Length
            && (info.LastWriteTime - entry.LastWriteTime.DateTime).Duration() <= ZipTimestampTolerance;
    }

    private static void CollectCopyJobs(
        string source, string destination,
        string? excludeAbsolutePath,
        List<string> excludePatterns,
        List<CopyJob> jobs)
    {
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            if (IsExcluded(name, excludePatterns)) continue;
            jobs.Add(new CopyJob(file, Path.Combine(destination, name)));
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            // 백업 대상 폴더 자체가 소스 하위에 있는 경우 무한 재귀 방지
            if (excludeAbsolutePath is not null &&
                dir.StartsWith(excludeAbsolutePath, StringComparison.OrdinalIgnoreCase))
                continue;

            var dirName = Path.GetFileName(dir);
            if (IsExcluded(dirName + "/", excludePatterns)) continue;

            CollectCopyJobs(dir, Path.Combine(destination, dirName), excludeAbsolutePath, excludePatterns, jobs);
        }
    }

    // File.Copy(CopyFileEx)는 스트림 복사보다 빠르고 수정 시각을 보존한다
    private static async Task CopyFilesAsync(
        List<CopyJob> jobs, string label,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        // 대상 폴더를 미리 만들어 병렬 복사 중 같은 폴더 생성 경합을 피한다
        foreach (var dir in jobs.Select(j => Path.GetDirectoryName(j.Destination)!)
                                .Distinct(StringComparer.OrdinalIgnoreCase))
            Directory.CreateDirectory(dir);

        var sw = Stopwatch.StartNew();
        var copied = 0;
        var lastReport = 0L;
        var total = jobs.Count;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(jobs, options, (job, _) =>
        {
            File.Copy(job.Source, job.Destination, overwrite: true);

            var done = Interlocked.Increment(ref copied);
            var now = sw.ElapsedMilliseconds;
            var last = Interlocked.Read(ref lastReport);
            if (now - last >= ProgressIntervalMs &&
                Interlocked.CompareExchange(ref lastReport, now, last) == last)
            {
                progress?.Report($"{label} 중 {done}/{total} — {Path.GetFileName(job.Source)}");
            }
            return ValueTask.CompletedTask;
        });
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "임시 파일 삭제 실패: {Path}", path);
        }
    }

    private static bool IsExcluded(string name, List<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.TrimEnd('/').Equals(name.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
