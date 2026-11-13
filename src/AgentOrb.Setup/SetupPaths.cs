namespace AgentOrb.Setup;

internal static class SetupPaths
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string LocalAppData { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string UserProfile { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string PackageRoot { get; } =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public static string DefaultInstallRoot =>
        Path.Combine(LocalAppData, "Programs", "AgentOrb");

    public static string DefaultAnodeRoot =>
        Path.Combine(LocalAppData, "Programs", "Anode");

    public static string StateRoot =>
        Path.Combine(LocalAppData, "AgentOrb");

    public static string StatePath =>
        Path.Combine(StateRoot, "setup-state.json");

    public static string DefaultAnodeArchive =>
        Path.Combine(PackageRoot, "anode", "anode-windows-x64.zip");

    public static string CodexConfigPath =>
        Path.Combine(UserProfile, ".codex", "config.toml");

    public static string AutostartRunKey =>
        @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string AgentOrbExecutable(string installRoot) =>
        Path.Combine(installRoot, "AgentOrb.exe");

    public static string BridgeExecutable(string installRoot) =>
        Path.Combine(installRoot, "AgentOrb.Bridge.exe");

    public static bool IsSafeInstallRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var programsRoot = Path.Combine(LocalAppData, "Programs") + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(programsRoot, PathComparison))
        {
            return false;
        }

        return string.Equals(
            Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar)),
            "AgentOrb",
            PathComparison);
    }

    public static bool IsWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, PathComparison);
    }
}
