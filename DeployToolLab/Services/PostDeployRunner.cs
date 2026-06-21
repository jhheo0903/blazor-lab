using System.Diagnostics;
using DeployTool.Models;

namespace DeployTool.Services;

public class PostDeployRunner(ILogger<PostDeployRunner> _logger)
{
    public async Task<List<string>> RunAsync(PostDeployExecutable exe, IProgress<string>? progress = null)
    {
        var logs = new List<string>();
        try
        {
            var header = $"▶ {(string.IsNullOrWhiteSpace(exe.DisplayName) ? exe.Path : exe.DisplayName)}";
            logs.Add(header);
            progress?.Report(header);

            var psi = new ProcessStartInfo
            {
                FileName = exe.Path,
                Arguments = exe.Arguments ?? string.Empty,
                WorkingDirectory = string.IsNullOrWhiteSpace(exe.WorkingDirectory)
                    ? Path.GetDirectoryName(exe.Path) ?? string.Empty
                    : exe.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)!;
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not { } line) return;
                logs.Add(line);
                progress?.Report(line);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not { } line) return;
                var err = $"[ERR] {line}";
                logs.Add(err);
                progress?.Report(err);
            };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync();

            var exit = $"종료 코드: {proc.ExitCode}";
            logs.Add(exit);
            progress?.Report(exit);
        }
        catch (Exception ex)
        {
            var err = $"실행 실패: {ex.Message}";
            logs.Add(err);
            progress?.Report(err);
            _logger.LogError(ex, "PostDeploy 실패: {Path}", exe.Path);
        }
        return logs;
    }
}
