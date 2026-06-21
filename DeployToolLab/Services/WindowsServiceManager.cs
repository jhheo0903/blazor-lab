using System.ServiceProcess;

namespace DeployTool.Services;

public class WindowsServiceManager(ILogger<WindowsServiceManager> _logger)
{
    public Task<(bool Ok, string Msg)> StopAsync(string name) => Task.Run(() => Control(name, false));
    public Task<(bool Ok, string Msg)> StartAsync(string name) => Task.Run(() => Control(name, true));

    private (bool, string) Control(string name, bool start)
    {
        try
        {
            using var sc = new ServiceController(name);
            var target = start ? ServiceControllerStatus.Running : ServiceControllerStatus.Stopped;
            if (sc.Status == target)
                return (true, $"{name}: 이미 {(start ? "실행 중" : "중지됨")}");
            if (start) sc.Start(); else sc.Stop();
            sc.WaitForStatus(target, TimeSpan.FromSeconds(30));
            _logger.LogInformation("서비스 {Action}: {Name}", start ? "시작" : "중지", name);
            return (true, $"{name}: {(start ? "시작" : "중지")} 완료");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "서비스 제어 실패: {Name}", name);
            return (false, $"{name}: {ex.Message}");
        }
    }
}
