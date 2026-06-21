namespace DeployTool.Models;

public class DeployConfig
{
    public List<string> WindowsServices { get; set; } = [];
    public List<string> IisAppPools { get; set; } = [];
    public List<PostDeployExecutable> PostDeployExecutables { get; set; } = [];
}

public class PostDeployExecutable
{
    public string DisplayName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
}
