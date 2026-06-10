namespace DeployTool.Models;

public enum ScopeMode
{
    FullCompare,
    ProjectSelect
}

public class FolderNode
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsSelected { get; set; } = true;
    public bool IsNewInDeploy { get; init; }
    public bool IsOnlyInProduction { get; init; }
    public string? GroupName { get; init; }
    public int EstimatedFileCount { get; init; }

    public string Badge => (IsNewInDeploy, IsOnlyInProduction) switch
    {
        (true, _) => "🆕",
        (_, true) => "🗑",
        _ => string.Empty
    };
}

public class ScopeSelection
{
    public ScopeMode Mode { get; set; } = ScopeMode.FullCompare;
    public List<FolderNode> AvailableFolders { get; set; } = [];
    public List<string> ExcludePatterns { get; set; } =
    [
        "log/", "Backup/", "temp/", "_quarantine/", "Elastic/",
        "*.log", "*.rb", "*.jar", "*.gemspec", "Config-Copy*/"
    ];

    public IEnumerable<FolderNode> SelectedFolders =>
        AvailableFolders.Where(f => f.IsSelected);

    public int EstimatedTotalFiles =>
        Mode == ScopeMode.FullCompare
            ? AvailableFolders.Sum(f => f.EstimatedFileCount)
            : SelectedFolders.Sum(f => f.EstimatedFileCount);
}
