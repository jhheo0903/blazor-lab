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

        try
        {
            var applyItems = session.ApplyItems.ToList();
            var total = applyItems.Count;

            await LogAsync(session, $"배포 시작 - 총 {total}개 항목");

            for (var i = 0; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var item = applyItems[i];
                var outcome = await ProcessItemAsync(session, item, i + 1, total, cancellationToken);

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
                }
            }

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
            PreflightChecker.ReleaseSessionLock(session.ProductionPath);
            session.Pause = null;
            session.IsDeploying = false;
            session.IsDeployDone = true;
            session.NotifyStateChanged();
        }
    }

    // 잠김 오류는 자동 재시도 후 일시중지하여 사용자 결정을 기다리고, 그 외 오류는 실패로 기록하고 다음 항목으로 넘어간다
    private async Task<ItemOutcome> ProcessItemAsync(
        DeploySession session, FileChangeItem item,
        int index, int total,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            IOException lockError;
            try
            {
                await ApplyItemWithAutoRetryAsync(item, session.ProductionPath, cancellationToken);
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
        FileChangeItem item, string productionPath, CancellationToken cancellationToken)
    {
        var retries = 0;
        while (true)
        {
            try
            {
                await ApplyItemAsync(item, productionPath);
                return;
            }
            catch (IOException ex) when (IsLockViolation(ex) && retries < LockAutoRetryCount)
            {
                retries++;
                await Task.Delay(LockAutoRetryDelay, cancellationToken);
            }
        }
    }

    private static async Task ApplyItemAsync(FileChangeItem item, string productionPath)
    {
        switch (item.Status)
        {
            case FileChangeStatus.Added:
            case FileChangeStatus.Modified:
                await ApplyFileAsync(item, productionPath);
                break;
            case FileChangeStatus.Deleted:
                QuarantineFile(item, productionPath);
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

    private static void QuarantineFile(FileChangeItem item, string productionPath)
    {
        if (item.ProductionFullPath is null) return;

        var quarantinePath = Path.Combine(productionPath, QUARANTINE_FOLDER, item.RelativePath);
        var quarantineDir = Path.GetDirectoryName(quarantinePath);
        if (quarantineDir is not null) Directory.CreateDirectory(quarantineDir);

        File.Move(item.ProductionFullPath, quarantinePath, overwrite: true);
    }

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
