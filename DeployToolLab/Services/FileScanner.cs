using DeployTool.Models;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace DeployTool.Services;

public class FileScanner(ILogger<FileScanner> _logger)
{
    private const string RootNodeName = ".";

    private static readonly HashSet<string> BinaryExtensions =
    [
        ".dll", ".exe", ".pdb", ".so", ".dylib", ".bin", ".zip", ".7z", ".rar"
    ];

    // Max parallel I/O tasks — bounded to avoid saturating network/SAN drives
    private static readonly ParallelOptions _parallelOpts = new()
    {
        MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount * 2, 16)
    };

    public async Task<List<FolderNode>> QuickScanTopLevelFoldersAsync(
        string productionPath, string deployPath)
    {
        return await Task.Run(() =>
        {
            var productionDirs = SafeGetDirectories(productionPath);
            var deployDirs = SafeGetDirectories(deployPath);

            var prodNames = productionDirs
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var deployNames = deployDirs
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var results = new ConcurrentBag<FolderNode>();

            Parallel.ForEach(deployDirs, _parallelOpts, dir =>
            {
                var name = Path.GetFileName(dir) ?? string.Empty;
                results.Add(new FolderNode
                {
                    Name = name,
                    FullPath = dir,
                    IsNewInDeploy = !prodNames.Contains(name),
                    EstimatedFileCount = SafeCountFiles(dir)
                });
            });

            Parallel.ForEach(productionDirs, _parallelOpts, dir =>
            {
                var name = Path.GetFileName(dir) ?? string.Empty;
                if (!deployNames.Contains(name))
                {
                    results.Add(new FolderNode
                    {
                        Name = name,
                        FullPath = dir,
                        IsOnlyInProduction = true,
                        EstimatedFileCount = SafeCountFiles(dir)
                    });
                }
            });

            var rootFileCount = SafeCountDistinctTopLevelFiles(productionPath, deployPath);
            if (rootFileCount > 0)
            {
                results.Add(new FolderNode
                {
                    Name = RootNodeName,
                    FullPath = deployPath,
                    IsRoot = true,
                    EstimatedFileCount = rootFileCount
                });
            }

            return results.OrderBy(f => f.Name).ToList();
        });
    }

    /// <summary>
    /// Full parallel scan with streaming progress.
    /// onPartialResult is called periodically so the UI can render partial trees
    /// before the full scan completes.
    /// </summary>
    public async Task<List<FileChangeItem>> ScanChangesAsync(
        string productionPath, string deployPath,
        ScopeSelection scope,
        IProgress<ScanProgress>? progress = null)
    {
        var bag = new ConcurrentBag<FileChangeItem>();
        var sw = Stopwatch.StartNew();

        // Throttled progress reporting — flush at most every 120 ms
        var lastFlush = 0L;
        var filesScanned = 0;

        void ReportThrottled(string currentFile)
        {
            Interlocked.Increment(ref filesScanned);
            var now = sw.ElapsedMilliseconds;
            if (now - Interlocked.Read(ref lastFlush) >= 120)
            {
                Interlocked.Exchange(ref lastFlush, now);
                progress?.Report(new ScanProgress(
                    Path.GetFileName(currentFile),
                    filesScanned,
                    bag.ToList()));
            }
        }

        var scanTasks = new List<Task>();

        if (scope.Mode == ScopeMode.FullCompare)
        {
            // Enumerate top-level subdirectories and scan each in parallel
            var prodTopDirs = SafeGetDirectories(productionPath);
            var deployTopDirs = SafeGetDirectories(deployPath);

            var allTopNames = prodTopDirs.Select(Path.GetFileName)
                .Concat(deployTopDirs.Select(Path.GetFileName))
                .Where(n => n is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Root-level files (not in subdirectories)
            scanTasks.Add(ScanOneLevelAsync(
                productionPath, deployPath,
                productionPath, deployPath,
                scope.ExcludePatterns, bag, ReportThrottled));

            // Each top-level directory in parallel
            foreach (var name in allTopNames)
            {
                if (IsExcluded(name + Path.DirectorySeparatorChar, scope.ExcludePatterns)) continue;
                var pd = Path.Combine(productionPath, name!);
                var dd = Path.Combine(deployPath, name!);
                scanTasks.Add(ScanDirectoryParallelAsync(
                    pd, dd, productionPath, deployPath,
                    scope.ExcludePatterns, bag, ReportThrottled));
            }
        }
        else
        {
            foreach (var folder in scope.SelectedFolders)
            {
                if (folder.IsRoot)
                {
                    scanTasks.Add(ScanOneLevelAsync(
                        productionPath, deployPath,
                        productionPath, deployPath,
                        scope.ExcludePatterns, bag, ReportThrottled));
                    continue;
                }

                var pd = Path.Combine(productionPath, folder.Name);
                var dd = Path.Combine(deployPath, folder.Name);
                scanTasks.Add(ScanDirectoryParallelAsync(
                    pd, dd, productionPath, deployPath,
                    scope.ExcludePatterns, bag, ReportThrottled));
            }
        }

        await Task.WhenAll(scanTasks);

        var results = bag.ToList();
        LinkPdbFiles(results);

        // Final progress flush
        progress?.Report(new ScanProgress(string.Empty, filesScanned, results));

        _logger.LogInformation("Scan complete: {Count} files in {Ms}ms", results.Count, sw.ElapsedMilliseconds);
        return results;
    }

    // Scans one directory level (files only, no recursion) — used for root level
    private static async Task ScanOneLevelAsync(
        string productionDir, string deployDir,
        string productionRoot, string deployRoot,
        List<string> excludePatterns,
        ConcurrentBag<FileChangeItem> results,
        Action<string> reportFile)
    {
        var prodFiles = Directory.Exists(productionDir) ? SafeGetFiles(productionDir) : [];
        var deployFiles = Directory.Exists(deployDir) ? SafeGetFiles(deployDir) : [];

        var prodMap = prodFiles.ToDictionary(
            f => Path.GetRelativePath(productionRoot, f),
            StringComparer.OrdinalIgnoreCase);
        var deployMap = deployFiles.ToDictionary(
            f => Path.GetRelativePath(deployRoot, f),
            StringComparer.OrdinalIgnoreCase);

        var tasks = new List<Task>();

        foreach (var (relPath, deployFile) in deployMap)
        {
            if (IsExcluded(relPath, excludePatterns)) continue;
            var rp = relPath;
            var df = deployFile;
            prodMap.TryGetValue(rp, out var pf);
            tasks.Add(ProcessFileAsync(rp, pf, df, results, reportFile));
        }

        foreach (var (relPath, prodFile) in prodMap)
        {
            if (IsExcluded(relPath, excludePatterns)) continue;
            if (!deployMap.ContainsKey(relPath))
            {
                results.Add(new FileChangeItem
                {
                    RelativePath = relPath,
                    ProductionFullPath = prodFile,
                    Status = FileChangeStatus.Deleted,
                    ProductionSize = GetFileSize(prodFile),
                    ProductionLastModified = GetLastModified(prodFile)
                });
                reportFile(relPath);
            }
        }

        await Task.WhenAll(tasks);
    }

    // Recursive parallel scan — processes all subdirectories concurrently
    private static async Task ScanDirectoryParallelAsync(
        string productionDir, string deployDir,
        string productionRoot, string deployRoot,
        List<string> excludePatterns,
        ConcurrentBag<FileChangeItem> results,
        Action<string> reportFile)
    {
        // Scan files at this level + enumerate subdirs simultaneously
        var filesTask = ScanOneLevelAsync(
            productionDir, deployDir,
            productionRoot, deployRoot,
            excludePatterns, results, reportFile);

        var prodSubDirs = Directory.Exists(productionDir) ? SafeGetDirectories(productionDir) : [];
        var deploySubDirs = Directory.Exists(deployDir) ? SafeGetDirectories(deployDir) : [];

        var allSubNames = prodSubDirs.Select(Path.GetFileName)
            .Concat(deploySubDirs.Select(Path.GetFileName))
            .Where(n => n is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var subTasks = new List<Task> { filesTask };

        foreach (var subName in allSubNames)
        {
            if (IsExcluded(subName + Path.DirectorySeparatorChar, excludePatterns)) continue;
            var sp = Path.Combine(productionDir, subName!);
            var sd = Path.Combine(deployDir, subName!);
            subTasks.Add(ScanDirectoryParallelAsync(
                sp, sd, productionRoot, deployRoot,
                excludePatterns, results, reportFile));
        }

        await Task.WhenAll(subTasks);
    }

    private static async Task ProcessFileAsync(
        string relPath, string? prodFile, string deployFile,
        ConcurrentBag<FileChangeItem> results,
        Action<string> reportFile)
    {
        reportFile(relPath);

        if (prodFile is null)
        {
            results.Add(new FileChangeItem
            {
                RelativePath = relPath,
                DeployFullPath = deployFile,
                Status = FileChangeStatus.Added,
                DeploySize = GetFileSize(deployFile),
                DeployLastModified = GetLastModified(deployFile),
                DeployFileVersion = GetFileVersion(deployFile),
                DeployAssemblyVersion = GetAssemblyVersion(deployFile)
            });
            return;
        }

        var prodInfo = new FileInfo(prodFile);
        var deployInfo = new FileInfo(deployFile);

        // Run file comparison and version reads concurrently
        var identicalTask = AreFilesIdenticalAsync(prodFile, deployFile, prodInfo, deployInfo);
        var prodVerTask = Task.Run(() => GetFileVersion(prodFile));
        var deployVerTask = Task.Run(() => GetFileVersion(deployFile));
        var prodAsmTask = Task.Run(() => GetAssemblyVersion(prodFile));
        var deployAsmTask = Task.Run(() => GetAssemblyVersion(deployFile));

        await Task.WhenAll(identicalTask, prodVerTask, deployVerTask, prodAsmTask, deployAsmTask);

        var prodFileVer = prodVerTask.Result;
        var deployFileVer = deployVerTask.Result;

        results.Add(new FileChangeItem
        {
            RelativePath = relPath,
            ProductionFullPath = prodFile,
            DeployFullPath = deployFile,
            Status = identicalTask.Result ? FileChangeStatus.Identical : FileChangeStatus.Modified,
            ProductionSize = prodInfo.Length,
            DeploySize = deployInfo.Length,
            ProductionLastModified = prodInfo.LastWriteTime,
            DeployLastModified = deployInfo.LastWriteTime,
            ProductionFileVersion = prodFileVer,
            DeployFileVersion = deployFileVer,
            ProductionAssemblyVersion = prodAsmTask.Result,
            DeployAssemblyVersion = deployAsmTask.Result,
            IsDowngrade = IsDowngrade(prodFileVer, deployFileVer)
        });
    }

    private static async Task<bool> AreFilesIdenticalAsync(
        string file1, string file2, FileInfo info1, FileInfo info2)
    {
        if (info1.Length != info2.Length) return false;

        // Binary files: size+date is sufficient (dll/exe timestamps are reliable)
        var ext = Path.GetExtension(file1).ToLowerInvariant();
        if (BinaryExtensions.Contains(ext))
            return info1.LastWriteTime == info2.LastWriteTime;

        if (info1.LastWriteTime == info2.LastWriteTime) return true;

        // Text file: byte comparison
        var bytes1 = await File.ReadAllBytesAsync(file1);
        var bytes2 = await File.ReadAllBytesAsync(file2);
        return bytes1.AsSpan().SequenceEqual(bytes2.AsSpan());
    }

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

    private static string[] SafeGetFiles(string path)
    {
        try { return Directory.GetFiles(path); }
        catch { return []; }
    }

    private static string[] SafeGetDirectories(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch { return []; }
    }

    private static int SafeCountFiles(string path)
    {
        try { return Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly).Length; }
        catch { return 0; }
    }

    private static int SafeCountDistinctTopLevelFiles(string productionPath, string deployPath)
    {
        var productionFiles = Directory.Exists(productionPath) ? SafeGetFiles(productionPath) : [];
        var deployFiles = Directory.Exists(deployPath) ? SafeGetFiles(deployPath) : [];

        return productionFiles
            .Select(Path.GetFileName)
            .Concat(deployFiles.Select(Path.GetFileName))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static long? GetFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return null; }
    }

    private static DateTime? GetLastModified(string path)
    {
        try { return new FileInfo(path).LastWriteTime; }
        catch { return null; }
    }

    private static string? GetFileVersion(string path)
    {
        try
        {
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            return FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch { return null; }
    }

    private static string? GetAssemblyVersion(string path)
    {
        try
        {
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            return FileVersionInfo.GetVersionInfo(path).ProductVersion;
        }
        catch { return null; }
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
