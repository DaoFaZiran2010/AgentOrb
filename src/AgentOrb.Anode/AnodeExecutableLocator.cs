namespace AgentOrb.Anode;

public static class AnodeExecutableLocator
{
    public static string? Resolve(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        var environmentPath = Environment.GetEnvironmentVariable("ANODE_EXE");
        if (File.Exists(environmentPath))
        {
            return environmentPath;
        }

        var executableName = OperatingSystem.IsWindows() ? "anode.exe" : "anode";
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var rawDirectory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = rawDirectory.Trim().Trim('"');
                if (directory.Length == 0)
                {
                    continue;
                }

                var candidate = CombineSafely(directory, executableName);
                if (candidate is not null && File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var candidate = CombineSafely(localAppData, Path.Combine("Programs", "Anode", "anode.exe"));
            if (candidate is not null && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? CombineSafely(string directory, string fileName)
    {
        try
        {
            return Path.Combine(directory, fileName);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
    }
}
