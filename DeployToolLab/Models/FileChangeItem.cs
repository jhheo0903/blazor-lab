namespace DeployTool.Models;


public enum FileChangeStatus
{
    Added,
    Deleted,
    Modified,
    Identical
}

public enum DeployDecisionValue
{
    Pending,
    Apply,
    Skip,
    KeepProduction
}

public class FileChangeItem
{
    public string RelativePath { get; init; } = string.Empty;
    public string? ProductionFullPath { get; init; }
    public string? DeployFullPath { get; init; }
    public FileChangeStatus Status { get; set; }
    public DeployDecisionValue Decision { get; set; } = DeployDecisionValue.Pending;

    public long? ProductionSize { get; init; }
    public long? DeploySize { get; init; }
    public DateTime? ProductionLastModified { get; init; }
    public DateTime? DeployLastModified { get; init; }

    public string? ProductionFileVersion { get; init; }
    public string? DeployFileVersion { get; init; }
    public string? ProductionAssemblyVersion { get; init; }
    public string? DeployAssemblyVersion { get; init; }
    public bool IsDowngrade { get; init; }

    public bool IsBinary => Path.GetExtension(RelativePath).ToLowerInvariant()
        is ".dll" or ".exe" or ".pdb" or ".so" or ".dylib";

    public bool IsPdb => Path.GetExtension(RelativePath).Equals(".pdb", StringComparison.OrdinalIgnoreCase);

    public string? LinkedPdbPath { get; set; }

    // When non-null, DeployExecutor writes this merged content instead of copying the deploy file
    public string? MergedJsonContent { get; set; }

    public string StatusIcon => Status switch
    {
        FileChangeStatus.Added => "🟢",
        FileChangeStatus.Deleted => "🔴",
        FileChangeStatus.Modified => "🟡",
        FileChangeStatus.Identical => "✅",
        _ => ""
    };
}
