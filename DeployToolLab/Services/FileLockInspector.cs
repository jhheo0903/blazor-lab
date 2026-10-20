using System.Runtime.InteropServices;
using System.Text;

namespace DeployTool.Services;

/// <summary>
/// Windows Restart Manager API로 파일을 열고 있는 프로세스를 조회합니다.
/// </summary>
public static class FileLockInspector
{
    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;
    private const int CchRmSessionKey = 32;
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles, string[] rgsFilenames,
        uint nApplications, RmUniqueProcess[]? rgApplications,
        uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RmProcessInfo[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    /// <summary>
    /// 지정한 파일들을 열고 있는 프로세스를 "이름 [PID n]" 형식으로 반환합니다.
    /// 조회에 실패하면(권한 부족 등) 빈 목록을 반환합니다.
    /// </summary>
    public static IReadOnlyList<string> GetLockingProcesses(IEnumerable<string?> paths)
    {
        var files = paths
            .OfType<string>()
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0) return [];

        var sessionKey = new StringBuilder(CchRmSessionKey + 1);
        if (RmStartSession(out var handle, 0, sessionKey) != ErrorSuccess) return [];

        try
        {
            if (RmRegisterResources(handle, (uint)files.Length, files, 0, null, 0, null) != ErrorSuccess)
                return [];

            uint count = 0;
            uint rebootReasons = 0;
            var result = RmGetList(handle, out var needed, ref count, null, ref rebootReasons);
            if (result != ErrorMoreData || needed == 0) return [];

            var infos = new RmProcessInfo[needed];
            count = needed;
            result = RmGetList(handle, out _, ref count, infos, ref rebootReasons);
            if (result != ErrorSuccess) return [];

            return infos.Take((int)count).Select(Describe).ToList();
        }
        finally
        {
            RmEndSession(handle);
        }
    }

    private static string Describe(RmProcessInfo info) =>
        string.IsNullOrWhiteSpace(info.strServiceShortName)
            ? $"{info.strAppName} [PID {info.Process.dwProcessId}]"
            : $"{info.strAppName} (서비스: {info.strServiceShortName}) [PID {info.Process.dwProcessId}]";
}
