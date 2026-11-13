using System.Text;

namespace AgentOrb.Setup;

internal static class CodexConfigManager
{
    private const string BeginMarker = "# >>> AgentOrb managed block >>>";
    private const string EndMarker = "# <<< AgentOrb managed block <<<";
    private const string AgentOrbSectionHeader = "[mcp_servers.agentorb]";

    public static bool IsManagedBlockPresent(string text) =>
        text.Contains(BeginMarker, StringComparison.Ordinal)
        || text.Contains(EndMarker, StringComparison.Ordinal);

    public static CodexConfigInstallResult Install(
        string configPath,
        string bridgePath,
        string agentId,
        string agentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgePath);
        ValidateShortValue(agentId, nameof(agentId));
        ValidateShortValue(agentName, nameof(agentName));

        var fullConfigPath = Path.GetFullPath(configPath);
        if (!File.Exists(fullConfigPath))
        {
            throw new FileNotFoundException("未找到 Codex 配置文件。", fullConfigPath);
        }

        var fullBridgePath = Path.GetFullPath(bridgePath);
        var original = File.ReadAllText(fullConfigPath);
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = NormalizeLines(original);
        var beginIndex = lines.FindIndex(line => string.Equals(line.Trim(), BeginMarker, StringComparison.Ordinal));
        var endIndex = lines.FindIndex(line => string.Equals(line.Trim(), EndMarker, StringComparison.Ordinal));

        if ((beginIndex >= 0) != (endIndex >= 0) || (beginIndex >= 0 && endIndex < beginIndex))
        {
            throw new InvalidDataException("Codex 配置中的 AgentOrb 标记不完整，已拒绝修改。");
        }

        if (beginIndex >= 0)
        {
            if (lines
                .Select((line, index) => (line, index))
                .Where(item => IsAgentOrbSectionLine(item.line))
                .Any(item => item.index < beginIndex || item.index > endIndex))
            {
                throw new InvalidDataException("Codex 配置中存在多个 AgentOrb MCP 段，已拒绝覆盖。");
            }
        }
        else if (lines.Any(IsAgentOrbSectionLine))
        {
            throw new InvalidDataException(
                "Codex 配置中已存在非 AgentOrb 管理的 [mcp_servers.agentorb] 段，已拒绝覆盖。");
        }

        var block = BuildBlock(newline, fullBridgePath, agentId, agentName).Split(newline);
        if (beginIndex >= 0)
        {
            lines.RemoveRange(beginIndex, endIndex - beginIndex + 1);
            lines.InsertRange(beginIndex, block);
        }
        else
        {
            if (lines.Count > 0 && lines[^1].Length != 0)
            {
                lines.Add(string.Empty);
            }

            lines.AddRange(block);
        }

        var updated = string.Join(newline, lines);
        if (original.EndsWith(newline, StringComparison.Ordinal) && !updated.EndsWith(newline, StringComparison.Ordinal))
        {
            updated += newline;
        }

        var backupPath = CreateBackup(fullConfigPath, original);
        WriteAtomically(fullConfigPath, updated);
        return new CodexConfigInstallResult(backupPath, beginIndex >= 0);
    }

    public static bool Uninstall(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        var fullConfigPath = Path.GetFullPath(configPath);
        if (!File.Exists(fullConfigPath))
        {
            return false;
        }

        var original = File.ReadAllText(fullConfigPath);
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = NormalizeLines(original);
        var beginIndex = lines.FindIndex(line => string.Equals(line.Trim(), BeginMarker, StringComparison.Ordinal));
        var endIndex = lines.FindIndex(line => string.Equals(line.Trim(), EndMarker, StringComparison.Ordinal));
        if (beginIndex < 0 || endIndex < beginIndex)
        {
            return false;
        }

        lines.RemoveRange(beginIndex, endIndex - beginIndex + 1);
        while (beginIndex < lines.Count && lines[beginIndex].Length == 0)
        {
            lines.RemoveAt(beginIndex);
        }

        var updated = string.Join(newline, lines);
        if (original.EndsWith(newline, StringComparison.Ordinal) && !updated.EndsWith(newline, StringComparison.Ordinal))
        {
            updated += newline;
        }

        WriteAtomically(fullConfigPath, updated);
        return true;
    }

    internal static string BuildBlock(
        string newline,
        string bridgePath,
        string agentId,
        string agentName)
    {
        var lines = new[]
        {
            BeginMarker,
            AgentOrbSectionHeader,
            $"command = {ToTomlString(bridgePath)}",
            $"args = [{ToTomlString("--agent-id")}, {ToTomlString(agentId)}, {ToTomlString("--agent-name")}, {ToTomlString(agentName)}]",
            "startup_timeout_sec = 30",
            "tool_timeout_sec = 420",
            EndMarker
        };
        return string.Join(newline, lines);
    }

    private static List<string> NormalizeLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

    private static bool IsAgentOrbSectionLine(string line) =>
        line.TrimStart().StartsWith("[mcp_servers.agentorb", StringComparison.Ordinal);

    private static string CreateBackup(string configPath, string content)
    {
        var directory = Path.GetDirectoryName(configPath)
            ?? throw new InvalidOperationException("Codex 配置文件缺少父目录。");
        var backupPath = Path.Combine(
            directory,
            $"{Path.GetFileName(configPath)}.agentorb-{DateTime.UtcNow:yyyyMMddHHmmssfff}.bak");
        File.WriteAllText(backupPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return backupPath;
    }

    private static void WriteAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("文件缺少父目录。");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string ToTomlString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        builder.Append(@"\u").Append(((int)character).ToString("X4"));
                    }
                    else
                    {
                        builder.Append(character);
                    }
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static void ValidateShortValue(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 120 || value.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException("参数包含无效控制字符或过长。", parameterName);
        }
    }
}

internal sealed record CodexConfigInstallResult(string BackupPath, bool ReplacedExistingBlock);
