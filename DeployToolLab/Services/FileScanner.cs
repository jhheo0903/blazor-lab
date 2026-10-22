using DeployTool.Models;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace DeployTool.Services;

public class FileScanner(ILogger<FileScanner> _logger)
{
    private static readonly HashSet<string> BinaryExtensions =
    [
        ".dll", ".exe", ".pdb", ".so", ".dylib", ".bin", ".zip", ".7z", ".rar"
    ];

    // 동시 파일 비교 수 상한 — 네트워크/SAN 드라이브와 스레드 풀 포화를 막는다
    private static readonly int MaxParallelism = Math.Min(Environment.ProcessorCount * 2, 16);

    private const int ProgressIntervalMs = 120;
    private const int CompareBufferSize = 81920;

    // 열거 시 함께 얻은 FileInfo(크기·수정 시각 캐시됨)를 그대로 들고 다녀 파일별 메타데이터 재조회를 피한다
    private sealed record FilePair(string RelativePath, FileInfo? ProductionFile, FileInfo? DeployFile);

    private static readonly EnumerationOptions DirectoryEntryOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,          // 기존 Directory.GetFiles와 동일하게 숨김/시스템 파일 포함
        RecurseSubdirectories = false
    };

    /// <summary>
    /// 운영/배포 양쪽 경로를 재귀적으로 탐색해 트리 형태의 FolderNode 목록을 반환합니다.
    /// </summary>
    public async Task<List<FolderNode>> BuildFolderTreeAsync(
        string productionPath, string deployPath)
    {
        return await Task.Run(() => BuildChildren(productionPath, deployPath, string.Empty, 0));
    }

    private static List<FolderNode> BuildChildren(
        string productionBase, string deployBase,
        string relativeParent, int depth)
    {
        var prodDir = Path.Combine(productionBase, relativeParent);
        var deployDir = Path.Combine(deployBase, relativeParent);

        var prodNames = Directory.Exists(prodDir)
            ? SafeGetDirectories(prodDir).Select(d => Path.GetFileName(d) ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var deployNames = Directory.Exists(deployDir)
            ? SafeGetDirectories(deployDir).Select(d => Path.GetFileName(d) ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        var allNames = prodNames.Union(deployNames, StringComparer.OrdinalIgnoreCase)
                                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                .ToList();

        var nodes = new List<FolderNode>(allNames.Count);
        foreach (var name in allNames)
        {
            var relPath = string.IsNullOrEmpty(relativeParent)
                ? name
                : relativeParent + Path.DirectorySeparatorChar + name;

            var children = BuildChildren(productionBase, deployBase, relPath, depth + 1);

            var fileCountDir = Directory.Exists(Path.Combine(deployBase, relPath))
                ? Path.Combine(deployBase, relPath)
                : Path.Combine(productionBase, relPath);

            nodes.Add(new FolderNode
            {
                Name = name,
                RelativePath = relPath,
                Depth = depth,
                IsExpanded = false,
                IsNewInDeploy = deployNames.Contains(name) && !prodNames.Contains(name),
                IsOnlyInProduction = prodNames.Contains(name) && !deployNames.Contains(name),
                EstimatedFileCount = SafeCountTopFiles(fileCountDir),
                Children = children
            });
        }
        return nodes;
    }

    /// <summary>
    /// 선택 범위의 파일을 병렬로 비교합니다.
    /// Blazor 회로의 SynchronizationContext를 점유하지 않도록 전체 작업을 스레드 풀에서 실행하고,
    /// 동시 비교 수를 제한해 스레드 풀 고갈(→ SignalR ping 지연 → 재연결)을 방지합니다.
    /// </summary>
    public Task<ScanResult> ScanChangesAsync(
        string productionPath, string deployPath,
        ScopeSelection scope,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => ScanChangesCoreAsync(productionPath, deployPath, scope, progress, cancellationToken),
            cancellationToken);
    }

    private async Task<ScanResult> ScanChangesCoreAsync(
        string productionPath, string deployPath,
        ScopeSelection scope,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        // 1단계: 비교 대상 파일 쌍 수집 (내용은 읽지 않음) — 전체 개수를 정확히 알 수 있다
        var emptyDeployOnlyDirs = new List<string>();
        var pairs = CollectFilePairs(productionPath, deployPath, scope, emptyDeployOnlyDirs);
        var totalFiles = pairs.Count;
        progress?.Report(new ScanProgress(string.Empty, 0, totalFiles, 0, 0, 0));

        // 2단계: 동시성 제한 병렬 비교
        var bag = new ConcurrentBag<FileChangeItem>();
        var filesScanned = 0;
        var addedCount = 0;
        var deletedCount = 0;
        var modifiedCount = 0;
        var lastFlush = 0L;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(pairs, options, async (pair, ct) =>
        {
            var item = await CompareFileAsync(pair, ct);
            bag.Add(item);

            switch (item.Status)
            {
                case FileChangeStatus.Added: Interlocked.Increment(ref addedCount); break;
                case FileChangeStatus.Deleted: Interlocked.Increment(ref deletedCount); break;
                case FileChangeStatus.Modified: Interlocked.Increment(ref modifiedCount); break;
            }

            var scanned = Interlocked.Increment(ref filesScanned);

            // 진행률 보고는 최대 ProgressIntervalMs 간격으로 한 스레드만 수행
            var now = sw.ElapsedMilliseconds;
            var last = Interlocked.Read(ref lastFlush);
            if (now - last >= ProgressIntervalMs &&
                Interlocked.CompareExchange(ref lastFlush, now, last) == last)
            {
                progress?.Report(new ScanProgress(
                    Path.GetFileName(pair.RelativePath), scanned, totalFiles,
                    Volatile.Read(ref addedCount), Volatile.Read(ref deletedCount), Volatile.Read(ref modifiedCount)));
            }
        });

        var results = bag.ToList();
        LinkPdbFiles(results);

        progress?.Report(new ScanProgress(
            string.Empty, filesScanned, totalFiles, addedCount, deletedCount, modifiedCount));

        _logger.LogInformation(
            "Scan complete: {Count} files, {EmptyDirs} empty deploy-only dirs in {Ms}ms",
            results.Count, emptyDeployOnlyDirs.Count, sw.ElapsedMilliseconds);
        return new ScanResult(results, emptyDeployOnlyDirs);
    }

    private static List<FilePair> CollectFilePairs(
        string productionRoot, string deployRoot, ScopeSelection scope,
        List<string> emptyDeployOnlyDirs)
    {
        var pairs = new List<FilePair>();
        var visitedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<string> startDirs = scope.Mode == ScopeMode.FullCompare
            ? [string.Empty]
            : GetSelectedRelativePaths(scope);

        foreach (var relativeDir in startDirs)
            CollectDirectory(productionRoot, deployRoot, relativeDir, scope.ExcludePatterns, pairs, emptyDeployOnlyDirs, visitedDirs);

        return pairs;
    }

    // 선택된 노드들의 상대 경로 (자식이 선택돼 있으면 자식 기준, 없으면 자신)
    private static List<string> GetSelectedRelativePaths(ScopeSelection scope)
    {
        return scope.FolderTree
            .SelectMany(n => n.Flatten())
            .Where(n => n.IsSelected && (n.Children.Count == 0 || n.Children.All(c => !c.IsSelected)))
            .Select(n => n.RelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void CollectDirectory(
        string productionRoot, string deployRoot, string relativeDir,
        List<string> excludePatterns,
        List<FilePair> pairs, List<string> emptyDeployOnlyDirs,
        HashSet<string> visitedDirs)
    {
        // 선택 경로가 서로 겹쳐도 같은 폴더를 두 번 수집하지 않는다
        if (!visitedDirs.Add(relativeDir)) return;

        var prodDir = Path.Combine(productionRoot, relativeDir);
        var deployDir = Path.Combine(deployRoot, relativeDir);
        var (prodFiles, prodSubDirs) = ReadDirectory(prodDir);
        var (deployFiles, deploySubDirs) = ReadDirectory(deployDir);

        // 배포에만 있는 빈 폴더 — 파일 복사로는 만들어지지 않으므로 기록해 두고 배포 시 생성한다
        if (relativeDir.Length > 0 &&
            deployFiles.Count == 0 && deploySubDirs.Count == 0 &&
            Directory.Exists(deployDir) && !Directory.Exists(prodDir))
        {
            emptyDeployOnlyDirs.Add(relativeDir);
        }

        foreach (var name in prodFiles.Keys.Union(deployFiles.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var relPath = Path.Combine(relativeDir, name);
            if (IsExcluded(relPath, excludePatterns)) continue;

            prodFiles.TryGetValue(name, out var prodFile);
            deployFiles.TryGetValue(name, out var deployFile);
            pairs.Add(new FilePair(relPath, prodFile, deployFile));
        }

        var subNames = prodSubDirs
            .Concat(deploySubDirs)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var subName in subNames)
        {
            if (IsExcluded(subName + Path.DirectorySeparatorChar, excludePatterns)) continue;
            CollectDirectory(
                productionRoot, deployRoot, Path.Combine(relativeDir, subName),
                excludePatterns, pairs, emptyDeployOnlyDirs, visitedDirs);
        }
    }

    // 한 번의 열거로 파일(크기·수정 시각 포함)과 하위 폴더 이름을 함께 얻는다.
    // (GetFiles + GetDirectories 2회 열거 + 파일별 FileInfo 조회를 대체)
    private static (Dictionary<string, FileInfo> Files, List<string> SubDirNames) ReadDirectory(string path)
    {
        var files = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        var subDirNames = new List<string>();
        try
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", DirectoryEntryOptions))
            {
                if (entry is FileInfo file)
                    files[file.Name] = file;
                else
                    subDirNames.Add(entry.Name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 한쪽에만 있는 폴더(DirectoryNotFound) 또는 접근 불가 — 빈 폴더로 취급
        }
        return (files, subDirNames);
    }

    private async Task<FileChangeItem> CompareFileAsync(FilePair pair, CancellationToken cancellationToken)
    {
        try
        {
            return await CompareFileCoreAsync(pair, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 운영 중인 프로세스가 잠근 파일 등 — 스캔 전체를 중단(회로 종료)시키지 않고 검토 대상으로 남긴다
            _logger.LogWarning(ex, "파일 비교 실패, 변경으로 간주합니다: {Path}", pair.RelativePath);
            return new FileChangeItem
            {
                RelativePath = pair.RelativePath,
                ProductionFullPath = pair.ProductionFile?.FullName,
                DeployFullPath = pair.DeployFile?.FullName,
                Status = pair switch
                {
                    { ProductionFile: null } => FileChangeStatus.Added,
                    { DeployFile: null } => FileChangeStatus.Deleted,
                    _ => FileChangeStatus.Modified
                }
            };
        }
    }

    private static async Task<FileChangeItem> CompareFileCoreAsync(FilePair pair, CancellationToken cancellationToken)
    {
        var (relPath, prodInfo, deployInfo) = pair;

        if (deployInfo is null)
        {
            return new FileChangeItem
            {
                RelativePath = relPath,
                ProductionFullPath = prodInfo!.FullName,
                Status = FileChangeStatus.Deleted,
                ProductionSize = prodInfo.Length,
                ProductionLastModified = prodInfo.LastWriteTime
            };
        }

        if (prodInfo is null)
        {
            var (addedFileVer, addedAsmVer) = GetVersions(deployInfo.FullName);
            return new FileChangeItem
            {
                RelativePath = relPath,
                DeployFullPath = deployInfo.FullName,
                Status = FileChangeStatus.Added,
                DeploySize = deployInfo.Length,
                DeployLastModified = deployInfo.LastWriteTime,
                DeployFileVersion = addedFileVer,
                DeployAssemblyVersion = addedAsmVer
            };
        }

        var isIdentical = await AreFilesIdenticalAsync(prodInfo, deployInfo, cancellationToken);
        if (isIdentical)
        {
            // 동일 파일은 배포 대상이 아니므로 버전 정보(PE 헤더 읽기)를 생략한다
            return new FileChangeItem
            {
                RelativePath = relPath,
                ProductionFullPath = prodInfo.FullName,
                DeployFullPath = deployInfo.FullName,
                Status = FileChangeStatus.Identical,
                ProductionSize = prodInfo.Length,
                DeploySize = deployInfo.Length,
                ProductionLastModified = prodInfo.LastWriteTime,
                DeployLastModified = deployInfo.LastWriteTime
            };
        }

        var (prodFileVer, prodAsmVer) = GetVersions(prodInfo.FullName);
        var (deployFileVer, deployAsmVer) = GetVersions(deployInfo.FullName);

        return new FileChangeItem
        {
            RelativePath = relPath,
            ProductionFullPath = prodInfo.FullName,
            DeployFullPath = deployInfo.FullName,
            Status = FileChangeStatus.Modified,
            ProductionSize = prodInfo.Length,
            DeploySize = deployInfo.Length,
            ProductionLastModified = prodInfo.LastWriteTime,
            DeployLastModified = deployInfo.LastWriteTime,
            ProductionFileVersion = prodFileVer,
            DeployFileVersion = deployFileVer,
            ProductionAssemblyVersion = prodAsmVer,
            DeployAssemblyVersion = deployAsmVer,
            IsDowngrade = IsDowngrade(prodFileVer, deployFileVer)
        };
    }

    private static async Task<bool> AreFilesIdenticalAsync(
        FileInfo info1, FileInfo info2, CancellationToken cancellationToken)
    {
        if (info1.Length != info2.Length) return false;

        // Binary files: size+date is sufficient (dll/exe timestamps are reliable)
        var ext = info1.Extension.ToLowerInvariant();
        if (BinaryExtensions.Contains(ext))
            return info1.LastWriteTime == info2.LastWriteTime;

        if (info1.LastWriteTime == info2.LastWriteTime) return true;

        return await AreContentsEqualAsync(info1.FullName, info2.FullName, cancellationToken);
    }

    // 파일 전체를 메모리에 올리지 않고 청크 단위로 비교.
    // 운영 프로세스가 열어 둔 파일도 읽을 수 있도록 공유 모드로 연다.
    private static async Task<bool> AreContentsEqualAsync(
        string file1, string file2, CancellationToken cancellationToken)
    {
        await using var stream1 = OpenSharedRead(file1);
        await using var stream2 = OpenSharedRead(file2);

        var buffer1 = new byte[CompareBufferSize];
        var buffer2 = new byte[CompareBufferSize];

        while (true)
        {
            var read1 = await stream1.ReadAtLeastAsync(buffer1, CompareBufferSize, throwOnEndOfStream: false, cancellationToken);
            var read2 = await stream2.ReadAtLeastAsync(buffer2, CompareBufferSize, throwOnEndOfStream: false, cancellationToken);

            if (read1 != read2) return false;
            if (read1 == 0) return true;
            if (!buffer1.AsSpan(0, read1).SequenceEqual(buffer2.AsSpan(0, read2))) return false;
        }
    }

    private static FileStream OpenSharedRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 0, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static void LinkPdbFiles(List<FileChangeItem> items)
    {
        var dllMap = items
            .Where(i => Path.GetExtension(i.RelativePath)
                .Equals(".dll", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                i => Path.ChangeExtension(i.RelativePath, null),
                StringComparer.OrdinalIgnoreCase);

        foreach (var pdb in items.Where(i => i.IsPdb))
        {
            var baseName = Path.ChangeExtension(pdb.RelativePath, null);
            if (dllMap.TryGetValue(baseName, out var dll))
                dll.LinkedPdbPath = pdb.RelativePath;
        }
    }

    private static bool IsExcluded(string relPath, List<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.EndsWith('/'))
            {
                var dirPart = pattern.TrimEnd('/');
                if (relPath.Contains(dirPart + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (pattern.StartsWith('*'))
            {
                var ext = pattern[1..];
                if (relPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (relPath.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string[] SafeGetDirectories(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch { return []; }
    }

    private static int SafeCountTopFiles(string path)
    {
        try { return Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly).Length; }
        catch { return 0; }
    }


    // FileVersion / ProductVersion을 한 번의 GetVersionInfo 호출로 읽는다
    private static (string? FileVersion, string? AssemblyVersion) GetVersions(string path)
    {
        try
        {
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return (null, null);
            var info = FileVersionInfo.GetVersionInfo(path);
            return (info.FileVersion, info.ProductVersion);
        }
        catch { return (null, null); }
    }

    private static bool IsDowngrade(string? currentVersion, string? newVersion)
    {
        if (currentVersion is null || newVersion is null) return false;
        if (Version.TryParse(currentVersion, out var current) &&
            Version.TryParse(newVersion, out var next))
            return next < current;
        return false;
    }
}
