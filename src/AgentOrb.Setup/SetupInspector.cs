using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace AgentOrb.Setup;

internal sealed record SetupInspection(
    string OsDescription,
    int BuildNumber,
    string Edition,
    bool Is64Bit,
    bool IsAdmin,
    bool RdpHostEnabled,
    bool ChildSessionsEnabled,
    string? AnodePath,
    bool RuntimeSourcePresent,
    bool RuntimeInstalled,
    bool CodexConfigPresent,
    IReadOnlyList<string> BlockingIssues,
    IReadOnlyList<string> Warnings)
{
    public bool CanInstall => BlockingIssues.Count == 0;
}

internal static class SetupInspector
{
    public static SetupInspection Inspect(string installRoot)
    {
        var build = ReadBuildNumber();
        var edition = ReadEdition();
        var is64 = Environment.Is64BitOperatingSystem;
        var isAdmin = IsAdministrator();
        var rdpDenied = ReadRdpDenied();
        var childSessionsEnabled = WindowsNative.TryGetChildSessionsEnabled(out var childEnabled) && childEnabled;
        var anodePath = FindAnodeExecutable();
        var runtimeSourcePresent = File.Exists(SetupPaths.AgentOrbExecutable(SetupPaths.PackageRoot))
            && File.Exists(SetupPaths.BridgeExecutable(SetupPaths.PackageRoot));
        var runtimeInstalled = File.Exists(SetupPaths.AgentOrbExecutable(installRoot))
            && File.Exists(SetupPaths.BridgeExecutable(installRoot));
        var codexConfigPresent = File.Exists(SetupPaths.CodexConfigPath);

        var blocking = new List<string>();
        var warnings = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            blocking.Add("当前系统不是 Windows。");
        }

        if (build < 19041)
        {
            blocking.Add($"Windows 版本过低（build {build}），需要 Windows 10 2004/build 19041 或更高版本。");
        }

        if (!IsSupportedEdition(edition))
        {
            blocking.Add($"当前版本“{edition}”不支持子会话，需要 Windows 10/11 Pro、Enterprise 或 Education。");
        }

        if (!is64)
        {
            blocking.Add("当前系统不是 x64，Anode 子会话运行时未验证 ARM64。");
        }

        if (!runtimeSourcePresent && !runtimeInstalled)
        {
            blocking.Add("安装包缺少 AgentOrb.exe 或 AgentOrb.Bridge.exe；请使用完整安装包。");
        }

        if (!isAdmin)
        {
            warnings.Add("安装或卸载需要管理员权限；check 可继续运行。");
        }

        if (rdpDenied != 0)
        {
            warnings.Add("远程桌面主机当前关闭；安装会启用它和 Windows 子会话。");
        }

        if (!childSessionsEnabled)
        {
            warnings.Add("Windows 子会话当前未启用；安装会调用 WTSEnableChildSessions(true)。");
        }

        if (anodePath is null)
        {
            warnings.Add("未检测到 Anode；安装器会下载固定版本并校验 SHA-256。");
        }

        if (!codexConfigPresent)
        {
            warnings.Add("未检测到 Codex 配置文件；安装将跳过客户端接入。");
        }

        return new SetupInspection(
            RuntimeInformation.OSDescription,
            build,
            edition,
            is64,
            isAdmin,
            rdpDenied == 0,
            childSessionsEnabled,
            anodePath,
            runtimeSourcePresent,
            runtimeInstalled,
            codexConfigPresent,
            blocking,
            warnings);
    }

    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static int ReadRdpDenied()
    {
        var value = ReadRegistryValue(
            @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server",
            "fDenyTSConnections",
            1);
        return value is int number ? number : 1;
    }

    public static string? FindAnodeExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("ANODE_EXE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var defaultPath = Path.Combine(SetupPaths.DefaultAnodeRoot, "anode.exe");
        if (File.Exists(defaultPath))
        {
            return defaultPath;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "anode.exe");
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static int ReadBuildNumber()
    {
        var value = ReadRegistryValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            "CurrentBuildNumber",
            null);
        return int.TryParse(value?.ToString(), out var build) ? build : Environment.OSVersion.Version.Build;
    }

    private static string ReadEdition()
    {
        var value = ReadRegistryValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            "EditionID",
            null);
        return value?.ToString() ?? "Unknown";
    }

    private static object? ReadRegistryValue(string keyPath, string valueName, object? fallback)
    {
        try
        {
            return Registry.GetValue(keyPath, valueName, fallback);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return fallback;
        }
    }

    private static bool IsSupportedEdition(string edition) =>
        edition.Contains("Professional", StringComparison.OrdinalIgnoreCase)
        || edition.Contains("Enterprise", StringComparison.OrdinalIgnoreCase)
        || edition.Contains("Education", StringComparison.OrdinalIgnoreCase);
}
