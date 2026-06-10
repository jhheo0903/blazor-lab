using DeployTool.Models;

namespace DeployTool.Services;

public class BackupService(ILogger<BackupService> logger)
{
    public async Task<string> CreateBackupAsync(
        string productionPath,
        ScopeSelection scope,
        IProgress<string>? progress = null)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupRoot = Path.Combine(productionPath, "Backup", $"backup_{timestamp}");
        Directory.CreateDirectory(backupRoot);

        if (scope.Mode == ScopeMode.FullCompare)
        {
            await CopyDirectoryAsync(productionPath, backupRoot, scope.ExcludePatterns, progress);
        }
        else
        {
            foreach (var folder in scope.SelectedFolders)
            {
                var srcFolder = Path.Combine(productionPath, folder.Name);
                var dstFolder = Path.Combine(backupRoot, folder.Name);
                if (Directory.Exists(srcFolder))
                    await CopyDirectoryAsync(srcFolder, dstFolder, scope.ExcludePatterns, progress);
            }
        }

        logger.LogInformation("백업 완료: {BackupPath}", backupRoot);
        return backupRoot;
    }

    public async Task RestoreBackupAsync(
        string backupPath, string productionPath,
        IProgress<string>? progress = null)
    {
        await CopyDirectoryAsync(backupPath, productionPath, [], progress);
        logger.LogInformation("롤백 완료: {BackupPath} -> {ProductionPath}", backupPath, productionPath);
    }

    private static async Task CopyDirectoryAsync(
        string source, string destination,
        List<string> excludePatterns,
        IProgress<string>? progress)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            var relPath = Path.GetFileName(file);
            if (IsExcluded(relPath, excludePatterns)) continue;

            var destFile = Path.Combine(destination, relPath);
            progress?.Report(file);
            await CopyFileAsync(file, destFile);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            var dirName = Path.GetFileName(dir);
            if (IsExcluded(dirName + "/", excludePatterns)) continue;

            var destDir = Path.Combine(destination, dirName);
            await CopyDirectoryAsync(dir, destDir, excludePatterns, progress);
        }
    }

    private static async Task CopyFileAsync(string source, string destination)
    {
        await using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await src.CopyToAsync(dst);
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
