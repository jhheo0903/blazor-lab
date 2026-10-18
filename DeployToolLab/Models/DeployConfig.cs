namespace DeployTool.Models;

public class DeployConfig
{
    public List<string> WindowsServices { get; set; } = [];
    public List<string> IisAppPools { get; set; } = [];
    public List<PostDeployExecutable> PostDeployExecutables { get; set; } = [];

    // 스캔 후 기본값을 스킵으로 지정할 파일 패턴 (목록에는 표시되며 사용자가 다시 적용으로 변경 가능)
    public List<string> SkipPatterns { get; set; } = [];
}

public class PostDeployExecutable
{
    public string DisplayName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
}
