using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bakabot.Helpers;

/// <summary>
/// 把子进程绑到一个"随本进程关闭而一起结束"的 Windows Job Object 上。
///
/// 为什么需要：启动器会拉起 ViaProxy（java）与机器人（node）等子进程。原先只在
/// App.OnExit 里逐个 Kill，一旦启动器被强关/崩溃/进程被任务管理器结束，清理代码
/// 根本不会执行，子进程就变成孤儿。ViaProxy 的孤儿会一直占用 ViaProxy.jar，
/// 导致"更新到最新版"永远报"文件正被占用"（2026-10-28 实测复现）。
///
/// Job Object 由内核保证：设置 JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE 后，只要最后一个
/// 指向该 job 的句柄被关闭（本进程结束即关闭），job 内所有进程都会被终止。
/// 因此即使启动器崩溃，也不会留下孤儿。
///
/// 失败时一律静默降级（返回 false），调用方继续使用原有的显式清理逻辑，不报错、不阻断。
/// </summary>
public static class JobObjectHelper
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    // 必须一直持有句柄：句柄关闭 = 杀光 job 内进程。用静态字段让它活到进程结束。
    private static IntPtr _job = IntPtr.Zero;
    private static readonly object _lock = new();
    private static bool _initialized;

    /// <summary>job 是否可用（初始化失败时为 false，调用方应降级）</summary>
    public static bool IsAvailable
    {
        get { lock (_lock) return _job != IntPtr.Zero; }
    }

    /// <summary>
    /// 创建并配置 job（只需调用一次，重复调用无副作用）。
    /// 失败时返回 false 并静默降级，不抛出。
    /// </summary>
    public static bool EnsureInitialized()
    {
        lock (_lock)
        {
            if (_initialized) return _job != IntPtr.Zero;
            _initialized = true;
            try
            {
                var job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return false;

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

                var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
                    {
                        CloseHandle(job);
                        return false;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }

                _job = job;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 把已启动的子进程加入 job。失败（job 不可用 / 进程已退出 / 权限问题）时返回 false，
    /// 调用方无需处理——原有的显式 Kill 逻辑仍然生效。
    /// </summary>
    public static bool TryAssign(Process? process)
    {
        if (process == null) return false;
        try
        {
            if (!EnsureInitialized()) return false;
            if (process.HasExited) return false;

            lock (_lock)
            {
                if (_job == IntPtr.Zero) return false;
                // Win8+ 支持嵌套 job，常规环境下都能成功；失败也不影响功能
                return AssignProcessToJobObject(_job, process.Handle);
            }
        }
        catch
        {
            return false;
        }
    }
}
