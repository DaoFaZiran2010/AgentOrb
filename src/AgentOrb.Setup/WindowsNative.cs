using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AgentOrb.Setup;

internal static class WindowsNative
{
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnableChildSessions([MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSIsChildSessionsEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    public static bool TryGetChildSessionsEnabled(out bool enabled)
    {
        enabled = false;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return WTSIsChildSessionsEnabled(out enabled);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    public static void EnableChildSessions(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows 子会话只在 Windows 上可用。");
        }

        if (!WTSEnableChildSessions(enabled))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法切换 Windows 子会话状态。");
        }
    }
}
