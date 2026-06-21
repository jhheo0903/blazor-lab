using System.Diagnostics;

namespace DeployTool.Services;

public class IisPoolManager(ILogger<IisPoolManager> _logger)
{
    private static readonly string AppCmd = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32", "inetsrv", "appcmd.exe");

    public Task<(bool Ok, string Msg)> StopAsync(string name) => RunAsync("stop", name);
    public Task<(bool Ok, string Msg)> StartAsync(string name) => RunAsync("start", name);

    private async Task<(bool, string)> RunAsync(string action, string name)
    {
        if (!File.Exists(AppCmd))
            return (false, $"appcmd.exe를 찾을 수 없습니다: {AppCmd}");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = AppCmd,
                Arguments = $"{action} apppool /apppool.name:\"{name}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var ok = proc.ExitCode == 0;
            _logger.LogInformation("IIS 풀 {Action} {Name}: 코드={Code}", action, name, proc.ExitCode);
            var detail = stderr.Trim() is { Length: > 0 } e ? e : stdout.Trim();
            return (ok, ok ? $"{name}: {action} 완료" : $"{name}: {detail}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IIS 풀 제어 실패: {Name}", name);
            return (false, $"{name}: {ex.Message}");
        }
    }
}
