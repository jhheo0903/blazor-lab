using DeployTool.Models;
using DeployTool.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace DeployTool.Services;

public class DeployExecutor(
    IHubContext<DeployLogHub> hubContext,
    ILogger<DeployExecutor> logger)
{
    private const string QUARANTINE_FOLDER = "_quarantine";

    public async Task ExecuteDeployAsync(
        DeploySession session,
        CancellationToken cancellationToken = default)
    {
        session.IsDeploying = true;
        session.DeployLog.Clear();

        var sessionId = Guid.NewGuid().ToString("N")[..8];
        PreflightChecker.AcquireSessionLock(session.ProductionPath, sessionId);

        try
        {
            var applyItems = session.ApplyItems.ToList();
            var total = applyItems.Count;
            var done = 0;

            await LogAsync(session, $"배포 시작 - 총 {total}개 항목");

            foreach (var item in applyItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await ProcessItemAsync(session, item);
                done++;
                await LogAsync(session, $"[{done}/{total}] {item.RelativePath}");
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
            session.IsDeploying = false;
            session.IsDeployDone = true;
        }
    }

    private async Task ProcessItemAsync(DeploySession session, FileChangeItem item)
    {
        try
        {
            switch (item.Status)
            {
                case FileChangeStatus.Added:
                case FileChangeStatus.Modified:
                    await ApplyFileAsync(item, session.ProductionPath);
                    break;
                case FileChangeStatus.Deleted:
                    await QuarantineFileAsync(item, session.ProductionPath);
                    break;
            }
        }
        catch (Exception ex)
        {
            await LogAsync(session, $"오류 [{item.RelativePath}]: {ex.Message}", isError: true);
            logger.LogError(ex, "파일 처리 오류: {RelativePath}", item.RelativePath);
        }
    }

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

    private static async Task QuarantineFileAsync(FileChangeItem item, string productionPath)
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
