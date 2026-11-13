using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentOrb.Desktop;

public sealed class SingleInstanceGuard : IDisposable
{
    private const string WindowTitlePrefix = "AgentOrb 悬浮监督";
    private const string DefaultMutexName = @"Global\AgentOrb.SingleInstance.v1";
    private const int SwRestore = 9;

    private readonly Mutex _mutex;
    private bool _isOwner;

    private SingleInstanceGuard(Mutex mutex, bool isOwner)
    {
        _mutex = mutex;
        _isOwner = isOwner;
    }

    public bool IsOwner => _isOwner;

    /// <summary>
    /// A second instance started inside an Agent seat/RDP session must exit quietly: its
    /// window lives in another session, so it can neither notify the user nor share the pipe.
    /// </summary>
    public static bool IsRunningInConsoleSession()
    {
        try
        {
            var consoleSession = WTSGetActiveConsoleSessionId();
            if (consoleSession == 0xFFFFFFFF)
            {
                return true;
            }

            return Process.GetCurrentProcess().SessionId == (int)consoleSession;
        }
        catch
        {
            return true;
        }
    }

    public static SingleInstanceGuard TryAcquire(string? mutexName = null)
    {
        var name = string.IsNullOrWhiteSpace(mutexName) ? DefaultMutexName : mutexName;
        var mutex = new Mutex(initiallyOwned: false, name);
        try
        {
            var acquired = mutex.WaitOne(TimeSpan.Zero);
            return new SingleInstanceGuard(mutex, acquired);
        }
        catch (AbandonedMutexException)
        {
            // A previous process ended without releasing the mutex; this process now owns it.
            return new SingleInstanceGuard(mutex, isOwner: true);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public bool ActivateExisting()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var existingHandle = FindExistingWindow();
            if (existingHandle != IntPtr.Zero)
            {
                _ = ShowWindow(existingHandle, SwRestore);
                _ = SetForegroundWindow(existingHandle);
                return true;
            }

            if (attempt < 2)
            {
                Thread.Sleep(150);
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (!_isOwner)
        {
            _mutex.Dispose();
            return;
        }

        _isOwner = false;
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    private static IntPtr FindExistingWindow()
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows(
            (handle, _) =>
            {
                if (!IsWindowVisible(handle))
                {
                    return true;
                }

                var length = GetWindowTextLengthW(handle);
                if (length <= 0)
                {
                    return true;
                }

                var titleBuilder = new StringBuilder(length + 1);
                if (GetWindowTextW(handle, titleBuilder, titleBuilder.Capacity) <= 0)
                {
                    return true;
                }

                if (titleBuilder.ToString().StartsWith(WindowTitlePrefix, StringComparison.Ordinal))
                {
                    result = handle;
                    return false;
                }

                return true;
            },
            IntPtr.Zero);

        return result;
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr handle, StringBuilder title, int capacity);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}
