using System.Text;
using System.Text.Json;

namespace AgentOrb.Setup;

internal sealed record SetupState
{
    public int SchemaVersion { get; init; } = 1;
    public required string InstallRoot { get; init; }
    public required string AnodeRoot { get; init; }
    public required string AgentVersion { get; init; }
    public string? AnodeVersion { get; init; }
    public string? CodexConfigPath { get; init; }
    public string? CodexBackupPath { get; init; }
    public int? PreviousRdpDeny { get; init; }
    public bool? PreviousChildSessions { get; init; }
    public string? PreviousAutostartValue { get; init; }
    public bool AutostartAdded { get; init; }
    public bool AnodeInstalledBySetup { get; init; }
    public DateTimeOffset InstalledAt { get; init; }
}

internal static class SetupStateStore
{
    public static SetupState? Load()
    {
        if (!File.Exists(SetupPaths.StatePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(SetupPaths.StatePath, Encoding.UTF8);
            var state = JsonSerializer.Deserialize<SetupState>(json);
            if (state is null || state.SchemaVersion != 1)
            {
                throw new InvalidDataException("安装状态文件版本无效。");
            }

            return state;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("安装状态文件损坏，已拒绝执行系统回滚。", exception);
        }
    }

    public static void Save(SetupState state)
    {
        Directory.CreateDirectory(SetupPaths.StateRoot);
        var temporary = SetupPaths.StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporary, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, SetupPaths.StatePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static void Delete()
    {
        if (File.Exists(SetupPaths.StatePath))
        {
            File.Delete(SetupPaths.StatePath);
        }
    }
}
