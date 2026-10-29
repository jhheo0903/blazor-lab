namespace DeployTool.Models;

public class DeployConfig
{
    public List<string> WindowsServices { get; set; } = [];
    public List<string> IisAppPools { get; set; } = [];

    // 후속 작업 목록 (JSON 키는 기존 설정 호환을 위해 postDeployExecutables 유지)
    public List<PostDeployExecutable> PostDeployExecutables { get; set; } = [];

    // 스캔 후 기본값을 스킵으로 지정할 파일 패턴 (목록에는 표시되며 사용자가 다시 적용으로 변경 가능)
    public List<string> SkipPatterns { get; set; } = [];
}

/// <summary>
/// 실행할 명령 1개.
/// Arguments에는 ForEachFile 사용 시 {key}, {file}, {fileName} 자리표시자를 쓸 수 있다.
/// </summary>
public class PostDeployStep
{
    public string Path { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;

    // 출력 인코딩 (예: "utf-8", "euc-kr", "949"). 비우면 시스템 콘솔 기본값
    public string OutputEncoding { get; set; } = string.Empty;

    // 지정하면 패턴에 맞는 파일마다 명령을 한 번씩 실행
    public FileIteration? ForEachFile { get; set; }
}

/// <summary>
/// 후속 작업 1건. Steps가 비어 있으면 자신의 Path/Arguments를 단일 명령으로 실행한다(기존 설정 호환).
/// </summary>
public class PostDeployExecutable : PostDeployStep
{
    public string DisplayName { get; set; } = string.Empty;
    public List<PostDeployStep> Steps { get; set; } = [];

    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? Path : DisplayName;

    public IReadOnlyList<PostDeployStep> GetSteps() => Steps.Count > 0 ? Steps : [this];
}

/// <summary>
/// Directory에서 Pattern(예: "pscmd.*.json", '*'는 1개)에 맞는 파일을 이름순으로 반복한다.
/// '*'에 해당하는 부분이 {key}가 된다. (pscmd.Exchange.json → key = Exchange)
/// </summary>
public class FileIteration
{
    public string Directory { get; set; } = string.Empty;
    public string Pattern { get; set; } = string.Empty;
}
