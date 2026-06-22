namespace DeployTool.Models;

public record ScanProgress(string CurrentFile, int FilesScanned, int TotalFiles, List<FileChangeItem> PartialResults);

public enum DeployStep
{
    PathConfig = 1,
    ScopeSelect = 2,
    Scan = 3,
    Review = 4,
    Backup = 5,
    Deploy = 6,
    Result = 7
}

public class DeploySession
{
    public DeployStep CurrentStep { get; set; } = DeployStep.PathConfig;

    public string ProductionPath { get; set; } = string.Empty;
    public string DeployPath { get; set; } = string.Empty;

    public ScopeSelection Scope { get; set; } = new();
    public List<FileChangeItem> Changes { get; set; } = [];

    public string? BackupPath { get; set; }
    public DateTime? BackupCreatedAt { get; set; }

    public List<DeployLogEntry> DeployLog { get; set; } = [];
    public bool IsDeploying { get; set; }
    public bool IsDeployDone { get; set; }

    public string? SessionLockId { get; set; }

    // 서비스 / IIS 풀 관리
    public HashSet<string> SelectedServices { get; set; } = [];
    public HashSet<string> SelectedAppPools { get; set; } = [];
    public List<string> StoppedServices { get; set; } = [];
    public List<string> StoppedAppPools { get; set; } = [];

    public IEnumerable<FileChangeItem> ChangedItems =>
        Changes.Where(c => c.Status != FileChangeStatus.Identical);

    public IEnumerable<FileChangeItem> ApplyItems =>
        Changes.Where(c => c.Decision == DeployDecisionValue.Apply);

    public IEnumerable<FileChangeItem> SkipItems =>
        Changes.Where(c => c.Decision == DeployDecisionValue.Skip);
}

public class DeployLogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Message { get; init; } = string.Empty;
    public bool IsError { get; init; }
    public bool IsWarning { get; init; }
}
