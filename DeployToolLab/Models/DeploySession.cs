using System.Collections.Concurrent;

namespace DeployTool.Models;

// 진행률 보고마다 결과 목록 전체를 복사하지 않도록 집계값만 전달
public record ScanProgress(
    string CurrentFile, int FilesScanned, int TotalFiles,
    int AddedCount, int DeletedCount, int ModifiedCount);

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

    // 사용자가 백업 없이 배포를 진행하기로 선택함 (롤백 불가)
    public bool IsBackupSkipped { get; set; }

    public ConcurrentBag<DeployLogEntry> DeployLog { get; set; } = [];
    public bool IsDeploying { get; set; }
    public bool IsDeployDone { get; set; }
    public int DeploySucceededCount { get; set; }

    // 잠긴 파일로 일시중지된 경우에만 값이 있음
    public DeployPauseState? Pause { get; set; }

    // 배포 진행 상태 변경 알림 — 백그라운드에서 변경되므로 구독자는 InvokeAsync로 렌더링해야 한다
    public event Action? StateChanged;
    public void NotifyStateChanged() => StateChanged?.Invoke();

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
