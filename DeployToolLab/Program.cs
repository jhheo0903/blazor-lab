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

// SignalR circuit options — extend timeouts so the page stays alive during idle
builder.Services.AddServerSideBlazor(options =>
{
    options.DetailedErrors = builder.Environment.IsDevelopment();
    options.DisconnectedCircuitMaxRetained = 100;
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(60);
    options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(2);
    options.MaxBufferedUnacknowledgedRenderBatches = 20;
});

// SignalR keep-alive — ping every 25 s, timeout after 5 missed pings (125 s)
builder.Services.AddSignalR(options =>
{
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(120);
    options.HandshakeTimeout      = TimeSpan.FromSeconds(30);
    options.KeepAliveInterval     = TimeSpan.FromSeconds(25);
    options.MaximumReceiveMessageSize = 64 * 1024; // 64 KB
});

builder.Services.AddScoped<DeploySession>();
builder.Services.AddScoped<FileScanner>();
builder.Services.AddScoped<DiffEngine>();
builder.Services.AddScoped<XmlDiffService>();
builder.Services.AddScoped<JsonDiffService>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddScoped<PreflightChecker>();
builder.Services.AddScoped<DeployExecutor>();
builder.Services.AddScoped<DbSchemaService>();

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

app.Run();
