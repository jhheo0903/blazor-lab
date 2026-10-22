using DeployTool.Models;
using DeployTool.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace DeployTool.Services;

public class DeployExecutor(
    IHubContext<DeployLogHub> hubContext,
    ILogger<DeployExecutor> logger)
{
    private const string QUARANTINE_FOLDER = "_quarantine";

    // 잠김 오류 시 자동 재시도 — IIS 작업자 프로세스가 풀 중지 후 늦게 종료되는 경우를 흡수한다
    private const int LockAutoRetryCount = 3;
    private static readonly TimeSpan LockAutoRetryDelay = TimeSpan.FromSeconds(2);

    // 일시중지 후 이 시간 동안 응답이 없으면 배포를 중단하고 세션 락을 해제한다
    private static readonly TimeSpan PauseTimeout = TimeSpan.FromMinutes(30);

    // Win32 오류 코드 (IOException.HResult 하위 16비트)
    private const int ERROR_SHARING_VIOLATION = 32;
    private const int ERROR_LOCK_VIOLATION = 33;

    private enum ItemOutcome
    {
        Succeeded,
        Failed,
        Aborted
    }

    public async Task ExecuteDeployAsync(
        DeploySession session,
        CancellationToken cancellationToken = default)
    {
        session.IsDeploying = true;
        session.DeploySucceededCount = 0;
        session.DeployLog.Clear();
        session.NotifyStateChanged();

        var sessionId = Guid.NewGuid().ToString("N")[..8];
        PreflightChecker.AcquireSessionLock(session.ProductionPath, sessionId);

        // 배포 회차별 격리 폴더 — 이전 회차의 격리 파일을 덮어쓰지 않도록 _quarantine\{시각}\ 아래로 옮긴다
        var quarantineRoot = Path.Combine(
            session.ProductionPath, QUARANTINE_FOLDER, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        var quarantinedCount = 0;

        // 삭제 예정 파일을 _quarantine으로 옮긴 원래 폴더 — 배포 후 비어 있으면 정리한다
        var vacatedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var applyItems = session.ApplyItems.ToList();
            var total = applyItems.Count;

            await LogAsync(session, $"배포 시작 - 총 {total}개 항목");

            for (var i = 0; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var item = applyItems[i];
                var outcome = await ProcessItemAsync(session, item, quarantineRoot, i + 1, total, cancellationToken);

                if (outcome == ItemOutcome.Aborted)
                {
                    await LogAsync(session,
                        $"배포 중단 — [{i + 1}/{total}] {item.RelativePath} 부터 남은 {total - i}개 항목은 처리하지 않았습니다.",
                        isError: true);
                    return;
                }

                if (outcome == ItemOutcome.Succeeded)
                {
                    session.DeploySucceededCount++;
                    await LogAsync(session, $"[{i + 1}/{total}] {item.RelativePath}");

                    if (item.Status == FileChangeStatus.Deleted)
                    {
                        quarantinedCount++;
                        if (Path.GetDirectoryName(Path.Combine(session.ProductionPath, item.RelativePath)) is { } dir)
                            vacatedDirs.Add(dir);
                    }
                }
            }

            await CreateEmptyDeployDirectoriesAsync(session);
            await LogAsync(session, "배포 완료");
        }
        catch (OperationCanceledException)
        {
            await LogAsync(session, "배포가 취소되었습니다.", isWarning: true);
        }
        catch (Exception ex)
        {
            await LogAsync(session, $"배포 중 오류 발생: {ex.Message}", isError: true);
            logger.LogError(ex, "배포 실행 오류");
        }
        finally
        {
            if (quarantinedCount > 0)
                await LogAsync(session, $"격리 파일 {quarantinedCount}개 → {quarantineRoot}");

            // 중단·취소된 경우에도 이미 옮긴 파일의 빈 폴더는 정리한다
            await RemoveEmptyDirectoriesAsync(session, vacatedDirs);

            PreflightChecker.ReleaseSessionLock(session.ProductionPath);
            session.Pause = null;
            session.IsDeploying = false;
            session.IsDeployDone = true;
            session.NotifyStateChanged();
        }
    }

    // 잠김 오류는 자동 재시도 후 일시중지하여 사용자 결정을 기다리고, 그 외 오류는 실패로 기록하고 다음 항목으로 넘어간다
    private async Task<ItemOutcome> ProcessItemAsync(
        DeploySession session, FileChangeItem item, string quarantineRoot,
        int index, int total,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            IOException lockError;
            try
            {
                await ApplyItemWithAutoRetryAsync(item, session.ProductionPath, quarantineRoot, cancellationToken);
                return ItemOutcome.Succeeded;
            }
            catch (IOException ex) when (IsLockViolation(ex))
            {
                lockError = ex;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await LogAsync(session, $"오류 [{item.RelativePath}]: {ex.Message}", isError: true);
                logger.LogError(ex, "파일 처리 오류: {RelativePath}", item.RelativePath);
                return ItemOutcome.Failed;
            }

            var action = await WaitForLockDecisionAsync(session, item, index, total, lockError, cancellationToken);
            switch (action)
            {
                case LockedFileAction.Retry:
                    await LogAsync(session, $"[재시도] {item.RelativePath}");
                    continue;

                case LockedFileAction.Skip:
                    await LogAsync(session, $"오류 [{item.RelativePath}]: 파일이 잠겨 있어 건너뜀", isError: true);
                    return ItemOutcome.Failed;

                default:
                    return ItemOutcome.Aborted;
            }
        }
    }

    private static async Task ApplyItemWithAutoRetryAsync(
        FileChangeItem item, string productionPath, string quarantineRoot,
        CancellationToken cancellationToken)
    {
        var retries = 0;
        while (true)
        {
            try
            {
                await ApplyItemAsync(item, productionPath, quarantineRoot);
                return;
            }
            catch (IOException ex) when (IsLockViolation(ex) && retries < LockAutoRetryCount)
            {
                retries++;
                await Task.Delay(LockAutoRetryDelay, cancellationToken);
            }
        }
    }

    private static async Task ApplyItemAsync(FileChangeItem item, string productionPath, string quarantineRoot)
    {
        switch (item.Status)
        {
            case FileChangeStatus.Added:
            case FileChangeStatus.Modified:
                await ApplyFileAsync(item, productionPath);
                break;
            case FileChangeStatus.Deleted:
                QuarantineFile(item, quarantineRoot);
                break;
        }
    }

    private async Task<LockedFileAction> WaitForLockDecisionAsync(
        DeploySession session, FileChangeItem item,
        int index, int total,
        IOException lockError,
        CancellationToken cancellationToken)
    {
        var lockedPath = item.Status == FileChangeStatus.Deleted
            ? item.ProductionFullPath ?? Path.Combine(session.ProductionPath, item.RelativePath)
            : Path.Combine(session.ProductionPath, item.RelativePath);

        // 대상 파일과 원본(배포) 파일 중 어느 쪽이 잠겼는지 모르므로 둘 다 조회
        var processes = FileLockInspector.GetLockingProcesses([lockedPath, item.DeployFullPath]);

        var pause = new DeployPauseState
        {
            Item = item,
            LockedFilePath = lockedPath,
            Index = index,
            Total = total,
            ErrorMessage = lockError.Message,
            Deadline = DateTime.Now.Add(PauseTimeout),
            LockingProcesses = processes
        };

        session.Pause = pause;
        await LogAsync(session,
            $"⏸ 일시중지 [{index}/{total}] 잠긴 파일: {item.RelativePath}" +
            (processes.Count > 0 ? $" — 사용 중: {string.Join(", ", processes)}" : string.Empty),
            isWarning: true);
        session.NotifyStateChanged();

        try
        {
            return await pause.WaitForDecisionAsync(PauseTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            await LogAsync(session,
                $"일시중지 후 {PauseTimeout.TotalMinutes:0}분 동안 응답이 없어 배포를 중단합니다.",
                isError: true);
            return LockedFileAction.Abort;
        }
        finally
        {
            session.Pause = null;
            session.NotifyStateChanged();
        }
    }

    private static bool IsLockViolation(IOException ex) =>
        (ex.HResult & 0xFFFF) is ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION;

    private static async Task ApplyFileAsync(FileChangeItem item, string productionPath)
    {
        var destPath = Path.Combine(productionPath, item.RelativePath);
        var destDir = Path.GetDirectoryName(destPath);
        if (destDir is not null) Directory.CreateDirectory(destDir);

        if (item.MergedJsonContent is not null)
        {
            await File.WriteAllTextAsync(destPath, item.MergedJsonContent, System.Text.Encoding.UTF8);
            return;
        }

        if (item.DeployFullPath is null) return;
        await CopyFileAsync(item.DeployFullPath, destPath);
    }

    private static void QuarantineFile(FileChangeItem item, string quarantineRoot)
    {
        if (item.ProductionFullPath is null) return;

        var quarantinePath = Path.Combine(quarantineRoot, item.RelativePath);
        var quarantineDir = Path.GetDirectoryName(quarantinePath);
        if (quarantineDir is not null) Directory.CreateDirectory(quarantineDir);

        File.Move(item.ProductionFullPath, quarantinePath, overwrite: true);
    }

    /// <summary>
    /// 배포 경로에만 있는 빈 폴더를 운영에도 만들어 폴더 구조를 배포 폴더와 맞춘다.
    /// (파일이 있는 폴더는 파일 복사 시 자동으로 생기므로 빈 폴더만 따로 처리)
    /// </summary>
    private async Task CreateEmptyDeployDirectoriesAsync(DeploySession session)
    {
        var created = new List<string>();
        foreach (var relDir in session.EmptyDeployOnlyDirectories)
        {
            var target = Path.Combine(session.ProductionPath, relDir);
            if (Directory.Exists(target)) continue;

            try
            {
                Directory.CreateDirectory(target);
                created.Add(relDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await LogAsync(session, $"빈 폴더 생성 실패 [{relDir}]: {ex.Message}", isWarning: true);
            }
        }

        if (created.Count > 0)
            await LogAsync(session, $"빈 폴더 {created.Count}개 생성: {string.Join(", ", created)}");
    }

    /// <summary>
    /// 파일을 옮겨 비게 된 폴더를 지우고, 상위 폴더도 비면 운영 루트 직전까지 차례로 지운다.
    /// 원래부터 다른 파일이나 하위 폴더가 있는 폴더, 배포 경로에도 있는 폴더는 건드리지 않는다
    /// (최종 폴더 구조를 배포 폴더와 같게 유지).
    /// </summary>
    private async Task RemoveEmptyDirectoriesAsync(DeploySession session, HashSet<string> vacatedDirs)
    {
        if (vacatedDirs.Count == 0) return;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(session.ProductionPath));
        var removed = new List<string>();

        // 깊은 폴더부터 처리해야 하위가 지워진 뒤 상위가 비었는지 판단할 수 있다
        foreach (var start in vacatedDirs.OrderByDescending(d => d.Length))
        {
            var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(start));

            // finally 블록에서 호출되므로 여기서 예외가 새어 나가면 세션 락이 풀리지 않는다 — 폴더 단위로 모두 처리
            try
            {
                // 배포 경로에 같은 폴더가 있으면 그 폴더와 상위 폴더는 모두 남긴다
                while (IsStrictlyUnder(dir, root) &&
                       Directory.Exists(dir) &&
                       !Directory.Exists(Path.Combine(session.DeployPath, Path.GetRelativePath(root, dir))) &&
                       !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    // recursive: false — 그 사이 파일이 생겼으면 실패하고 남겨 둔다
                    Directory.Delete(dir, recursive: false);
                    removed.Add(Path.GetRelativePath(root, dir));
                    dir = Path.GetDirectoryName(dir) ?? root;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "빈 폴더 삭제 실패: {Directory}", dir);
            }
        }

        if (removed.Count > 0)
            await LogAsync(session, $"빈 폴더 {removed.Count}개 정리: {string.Join(", ", removed)}");
    }

    private static bool IsStrictlyUnder(string path, string root) =>
        path.Length > root.Length &&
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private async Task LogAsync(DeploySession session, string message,
        bool isError = false, bool isWarning = false)
    {
        var entry = new DeployLogEntry
        {
            Message = message,
            IsError = isError,
            IsWarning = isWarning
        };
        session.DeployLog.Add(entry);

        await hubContext.Clients.All.SendAsync("ReceiveLog", message, isError, isWarning);

        if (isError) logger.LogError("{Message}", message);
        else if (isWarning) logger.LogWarning("{Message}", message);
        else logger.LogInformation("{Message}", message);
    }

    // 대상 파일이 잠겨 있으면 FileMode.Create로 여는 시점에 실패하므로 기존 내용이 잘리지 않는다 (부분 쓰기 없음)
    private static async Task CopyFileAsync(string source, string destination)
    {
        await using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await src.CopyToAsync(dst);
    }

    public static void CleanQuarantine(string productionPath)
    {
        var quarantine = Path.Combine(productionPath, QUARANTINE_FOLDER);
        if (Directory.Exists(quarantine))
            Directory.Delete(quarantine, recursive: true);
    }
}
