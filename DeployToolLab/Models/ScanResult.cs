namespace DeployTool.Models;

/// <param name="Changes">파일별 비교 결과</param>
/// <param name="EmptyDeployOnlyDirectories">
/// 배포 경로에만 있는 빈 폴더(상대 경로). 파일 복사로는 생기지 않으므로 배포 시 따로 만든다.
/// </param>
public sealed record ScanResult(
    List<FileChangeItem> Changes,
    List<string> EmptyDeployOnlyDirectories);
