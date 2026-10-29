using System.Diagnostics;
using System.Text;
using DeployTool.Models;

namespace DeployTool.Services;

/// <summary>
/// 후속 작업(설정 파일의 postDeployExecutables)을 실행하고 출력을 진행 로그로 전달합니다.
/// </summary>
public class PostDeployRunner(ILogger<PostDeployRunner> _logger)
{
    static PostDeployRunner()
    {
        // euc-kr(949) 등 코드 페이지 인코딩 사용을 위해 등록
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// 작업의 모든 명령을 순서대로 실행합니다. 하나라도 실패(종료 코드 ≠ 0, 실행 오류)하면 false.
    /// 실패해도 나머지 명령은 계속 실행합니다(PowerShell 스크립트와 동일한 동작).
    /// </summary>
    public async Task<bool> RunAsync(
        PostDeployExecutable task,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report($"▶ {task.Name}");

        var succeeded = 0;
        var failed = 0;

        foreach (var step in task.GetSteps())
        {
            if (step.ForEachFile is { } iteration)
            {
                var targets = ResolveFiles(iteration, progress, ref failed);
                foreach (var (key, file) in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var args = ExpandArguments(step.Arguments, key, file);
                    if (await RunProcessAsync(step, args, progress, cancellationToken)) succeeded++;
                    else failed++;
                }
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await RunProcessAsync(step, step.Arguments, progress, cancellationToken)) succeeded++;
                else failed++;
            }
        }

        var ok = failed == 0;
        progress?.Report($"{(ok ? "✔" : "✖")} {task.Name} — 성공 {succeeded}건, 실패 {failed}건");
        return ok;
    }

    private static List<(string Key, string File)> ResolveFiles(
        FileIteration iteration, IProgress<string>? progress, ref int failed)
    {
        var pattern = iteration.Pattern;
        var star = pattern.IndexOf('*');
        if (star < 0 || pattern.IndexOf('*', star + 1) >= 0)
        {
            progress?.Report($"[ERR] 파일 패턴에는 '*'가 정확히 1개 있어야 합니다: {pattern}");
            failed++;
            return [];
        }

        if (!Directory.Exists(iteration.Directory))
        {
            progress?.Report($"[ERR] 폴더가 없습니다: {iteration.Directory}");
            failed++;
            return [];
        }

        var prefix = pattern[..star];
        var suffix = pattern[(star + 1)..];
        var targets = new List<(string, string)>();

        var names = Directory.EnumerateFiles(iteration.Directory, pattern)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            // Windows 와일드카드 특성상 의도와 다른 이름이 섞일 수 있으므로 접두/접미사를 다시 확인
            if (name.Length > prefix.Length + suffix.Length &&
                name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var key = name[prefix.Length..^suffix.Length];
                targets.Add((key, Path.Combine(iteration.Directory, name)));
            }
            else
            {
                progress?.Report($"잘못된 파일 이름 형식: {name}. 예상 형식: {pattern}");
            }
        }

        if (targets.Count == 0)
            progress?.Report($"대상 파일 없음: {Path.Combine(iteration.Directory, pattern)}");

        return targets;
    }

    private static string ExpandArguments(string arguments, string key, string file) =>
        arguments
            .Replace("{key}", key, StringComparison.OrdinalIgnoreCase)
            .Replace("{file}", file, StringComparison.OrdinalIgnoreCase)
            .Replace("{fileName}", Path.GetFileName(file), StringComparison.OrdinalIgnoreCase);

    private async Task<bool> RunProcessAsync(
        PostDeployStep step, string arguments,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report($"> {step.Path} {arguments}");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = step.Path,
                Arguments = arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(step.WorkingDirectory)
                    ? Path.GetDirectoryName(step.Path) ?? string.Empty
                    : step.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (ResolveEncoding(step.OutputEncoding) is { } encoding)
            {
                psi.StandardOutputEncoding = encoding;
                psi.StandardErrorEncoding = encoding;
            }

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("프로세스를 시작하지 못했습니다.");

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is { } line) progress?.Report(line);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { } line) progress?.Report($"[ERR] {line}");
            };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(cancellationToken);

            var ok = proc.ExitCode == 0;
            progress?.Report(ok ? $"종료 코드: {proc.ExitCode}" : $"[ERR] 종료 코드: {proc.ExitCode}");
            return ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            progress?.Report($"[ERR] 실행 실패: {ex.Message}");
            _logger.LogError(ex, "후속 작업 실행 실패: {Path} {Arguments}", step.Path, arguments);
            return false;
        }
    }

    private static Encoding? ResolveEncoding(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return int.TryParse(name, out var codePage)
            ? Encoding.GetEncoding(codePage)
            : Encoding.GetEncoding(name);
    }
}
