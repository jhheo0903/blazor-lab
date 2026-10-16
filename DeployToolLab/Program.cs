using DeployTool.Hubs;
using DeployTool.Models;
using DeployTool.Services;
using Microsoft.AspNetCore.Components.Server;

// WinForms 다이얼로그(FolderBrowserDialog) 사용을 위한 초기화
System.Windows.Forms.Application.EnableVisualStyles();
System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

var builder = WebApplication.CreateBuilder(args);

// When running from source in non-Development environments,
// static web assets (e.g., /_framework/*) are not enabled automatically.
if (!builder.Environment.IsDevelopment())
{
    builder.WebHost.UseStaticWebAssets();
}

builder.Services.AddRazorPages();

// SignalR keep-alive / timeout
// - KeepAliveInterval은 클라이언트 기본값(15s)과 일치시키고, 클라이언트 ServerTimeout(_Host.cshtml: 60s)은 그 2배 이상으로 둔다.
//   (기존 25s ping은 클라이언트 기본 ServerTimeout 30s와 여유가 5s뿐이라 조금만 지연돼도 연결이 끊겼다.)
// - ClientTimeoutInterval은 백그라운드 탭에서 브라우저가 타이머를 최대 1분 단위로 늦추는 경우를 견딜 수 있게 넉넉히 잡는다.
static void ConfigureHub(Microsoft.AspNetCore.SignalR.HubOptions options)
{
    options.KeepAliveInterval         = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval     = TimeSpan.FromSeconds(180);
    options.HandshakeTimeout          = TimeSpan.FromSeconds(30);
    options.MaximumReceiveMessageSize = 4 * 1024 * 1024; // 4 MB
}

// SignalR circuit options — extend timeouts so the page stays alive during idle
builder.Services.AddServerSideBlazor(options =>
{
    options.DetailedErrors = builder.Environment.IsDevelopment();
    options.DisconnectedCircuitMaxRetained = 100;
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(60);
    options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(2);
    options.MaxBufferedUnacknowledgedRenderBatches = 20;
}).AddHubOptions(ConfigureHub);

// DeployLogHub 등 일반 허브에도 동일한 설정 적용
builder.Services.AddSignalR(ConfigureHub);

builder.Services.AddScoped<DeploySession>();
builder.Services.AddScoped<FileScanner>();
builder.Services.AddScoped<DiffEngine>();
builder.Services.AddScoped<XmlDiffService>();
builder.Services.AddScoped<JsonDiffService>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddScoped<PreflightChecker>();
builder.Services.AddScoped<DeployExecutor>();
builder.Services.AddScoped<DbSchemaService>();
builder.Services.AddSingleton<DeployConfigService>();
builder.Services.AddScoped<WindowsServiceManager>();
builder.Services.AddScoped<IisPoolManager>();
builder.Services.AddScoped<PostDeployRunner>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub(options =>
{
    // Match the SignalR hub timeout settings
    options.CloseOnAuthenticationExpiration = false;
});
app.MapHub<DeployLogHub>("/deployhub");
app.MapFallbackToPage("/_Host");

await app.RunAsync();
