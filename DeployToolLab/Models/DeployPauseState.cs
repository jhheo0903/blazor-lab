namespace DeployTool.Models;

public enum LockedFileAction
{
    Retry,
    Skip,
    Abort
}

/// <summary>
/// 잠긴 파일 때문에 배포가 일시중지된 상태. 사용자가 조치 방법을 선택할 때까지 배포 루프가 대기한다.
/// </summary>
public sealed class DeployPauseState
{
    private readonly TaskCompletionSource<LockedFileAction> _decision =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required FileChangeItem Item { get; init; }
    public required string LockedFilePath { get; init; }
    public required int Index { get; init; }          // 1부터 시작
    public required int Total { get; init; }
    public required string ErrorMessage { get; init; }
    public required DateTime Deadline { get; init; }  // 이 시각까지 응답이 없으면 배포 중단
    public IReadOnlyList<string> LockingProcesses { get; init; } = [];

    public void Resolve(LockedFileAction action) => _decision.TrySetResult(action);

    public Task<LockedFileAction> WaitForDecisionAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _decision.Task.WaitAsync(timeout, cancellationToken);
}
