using DeployTool.Models;

namespace DeployTool.Services;

public class PreflightChecker(ILogger<PreflightChecker> _logger)
{
    private const string LOCK_FILE_NAME = ".deploy.lock";

    /// <param name="backupPath">백업을 건너뛴 경우 null — 용량 확인 대신 경고 항목을 추가한다.</param>
    /// <param name="scannedItems">스캔 결과 전체 — 백업 범위 크기를 디스크 재탐색 없이 계산하는 데 사용한다.</param>
    /// <remarks>
    /// 파일 잠금 확인은 하지 않는다 — 서비스/풀 중지 전이라 결과가 의미 없고 시간이 오래 걸린다.
    /// 잠긴 파일은 배포 중 DeployExecutor가 감지해 일시중지한다.
    /// </remarks>
    public async Task<List<PreflightCheckResult>> RunChecksAsync(
        string productionPath, string? backupPath,
        IEnumerable<FileChangeItem> scannedItems)
    {
        var results = new List<PreflightCheckResult>();

        results.Add(await CheckWritePermissionAsync(productionPath));
        results.Add(backupPath is null
            ? new PreflightCheckResult
            {
                CheckName = "백업 확인",
                IsPassed = false,
                IsWarning = true,
                Message = "백업을 건너뛰었습니다. 배포 후 롤백할 수 없습니다."
            }
            : CheckBackupDiskSpace(backupPath, scannedItems));
        results.Add(CheckSessionLock(productionPath));

        return results;
    }

    private static async Task<PreflightCheckResult> CheckWritePermissionAsync(string productionPath)
    {
        var testFile = Path.Combine(productionPath, $".write_test_{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(testFile, "test");
            File.Delete(testFile);
            return new PreflightCheckResult
            {
                CheckName = "쓰기 권한 확인",
                IsPassed = true,
                Message = "운영 폴더 쓰기 가능"
            };
        }
        catch (Exception ex)
        {
            return new PreflightCheckResult
            {
                CheckName = "쓰기 권한 확인",
                IsPassed = false,
                Message = $"쓰기 권한 없음: {ex.Message}"
            };
        }
    }

    // 백업은 복사본 폴더를 만든 뒤 ZIP으로 압축하므로, 최악의 경우 범위 크기의 약 2배가 일시적으로 필요하다
    private const double BackupSpaceFactor = 2.0;

    private static PreflightCheckResult CheckBackupDiskSpace(
        string backupPath, IEnumerable<FileChangeItem> scannedItems)
    {
        try
        {
            var backupDrive = new DriveInfo(Path.GetPathRoot(backupPath) ?? backupPath);
            // 스캔 시 이미 수집한 운영 파일 크기 합계 — 운영 폴더 전체를 다시 탐색하지 않는다
            var scopeSize = scannedItems.Sum(i => i.ProductionSize ?? 0);
            var requiredSize = (long)(scopeSize * BackupSpaceFactor);
            var freeSpace = backupDrive.AvailableFreeSpace;
            var isPassed = freeSpace > requiredSize;

            return new PreflightCheckResult
            {
                CheckName = "백업 디스크 용량 확인",
                IsPassed = isPassed,
                IsWarning = !isPassed,
                Message = isPassed
                    ? $"여유 공간 충분 ({freeSpace / 1024 / 1024}MB 사용 가능)"
                    : $"디스크 공간 부족 ({freeSpace / 1024 / 1024}MB 사용 가능, {requiredSize / 1024 / 1024}MB 필요 예상)"
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new PreflightCheckResult
            {
                CheckName = "백업 디스크 용량 확인",
                IsPassed = false,
                Message = $"용량 확인 실패: {ex.Message}"
            };
        }
    }

    private static PreflightCheckResult CheckSessionLock(string productionPath)
    {
        var lockFile = Path.Combine(productionPath, LOCK_FILE_NAME);
        if (File.Exists(lockFile))
        {
            try
            {
                var lockInfo = File.ReadAllText(lockFile);
                return new PreflightCheckResult
                {
                    CheckName = "동시 배포 세션 확인",
                    IsPassed = false,
                    Message = $"다른 배포 세션이 진행 중입니다: {lockInfo}"
                };
            }
            catch
            {
                return new PreflightCheckResult
                {
                    CheckName = "동시 배포 세션 확인",
                    IsPassed = false,
                    Message = "락 파일 확인 실패"
                };
            }
        }

        return new PreflightCheckResult
        {
            CheckName = "동시 배포 세션 확인",
            IsPassed = true,
            Message = "다른 배포 세션 없음"
        };
    }

    public static void AcquireSessionLock(string productionPath, string sessionId)
    {
        var lockFile = Path.Combine(productionPath, LOCK_FILE_NAME);
        File.WriteAllText(lockFile, $"{sessionId} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }

    public static void ReleaseSessionLock(string productionPath)
    {
        var lockFile = Path.Combine(productionPath, LOCK_FILE_NAME);
        if (File.Exists(lockFile)) File.Delete(lockFile);
    }
}
