using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace AgentOrb.Setup;

internal static class SetupEngine
{
    private const string AutostartValueName = "AgentOrb";
    private static readonly TimeSpan AnodeSetupTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AnodeQuitTimeout = TimeSpan.FromSeconds(45);

    public static async Task InstallAsync(
        SetupOptions options,
        Action<string> report,
        CancellationToken cancellationToken)
    {
        if (!SetupInspector.IsAdministrator())
        {
            throw new UnauthorizedAccessException("安装需要管理员权限。");
        }

        var inspection = SetupInspector.Inspect(options.InstallRoot);
        if (!inspection.CanInstall)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, inspection.BlockingIssues));
        }

        EnsureInstalledProgramsNotRunning(options.InstallRoot);
        var existingState = SetupStateStore.Load();
        if (existingState is not null
            && !string.Equals(
                Path.GetFullPath(existingState.InstallRoot),
                Path.GetFullPath(options.InstallRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("检测到另一安装目录的状态，已拒绝覆盖。");
        }

        var installRoot = Path.GetFullPath(options.InstallRoot);
        var installedDesktop = SetupPaths.AgentOrbExecutable(installRoot);
        var previousRdpDeny = existingState?.PreviousRdpDeny ?? SetupInspector.ReadRdpDenied();
        var previousChildSessions = existingState?.PreviousChildSessions
            ?? (WindowsNative.TryGetChildSessionsEnabled(out var childEnabled) && childEnabled);
        var previousAutostart = existingState is not null
            ? existingState.PreviousAutostartValue
            : ReadAutostartValue();
        if (IsOurAutostartValue(previousAutostart, installedDesktop))
        {
            previousAutostart = null;
        }

        var anode = await AnodeProvisioner.EnsureAsync(options, report, cancellationToken).ConfigureAwait(false);
        report("复制 AgentOrb 运行时...");
        Directory.CreateDirectory(installRoot);
        CopyRuntimeFile("AgentOrb.exe", installRoot);
        CopyRuntimeFile("AgentOrb.Bridge.exe", installRoot);

        var installedBridge = SetupPaths.BridgeExecutable(installRoot);
        if (!File.Exists(installedDesktop) || !File.Exists(installedBridge))
        {
            throw new IOException("AgentOrb 运行时复制不完整。");
        }

        var preliminary = new SetupState
        {
            InstallRoot = installRoot,
            AnodeRoot = Path.GetDirectoryName(anode.ExecutablePath)!,
            AgentVersion = FileVersionInfo.GetVersionInfo(installedDesktop).FileVersion ?? "unknown",
            AnodeVersion = AnodeProvisioner.SupportedVersion,
            PreviousRdpDeny = previousRdpDeny,
            PreviousChildSessions = previousChildSessions,
            PreviousAutostartValue = previousAutostart,
            AnodeInstalledBySetup = existingState?.AnodeInstalledBySetup == true || anode.InstalledBySetup,
            CodexConfigPath = !options.SkipCodex && File.Exists(SetupPaths.CodexConfigPath)
                ? SetupPaths.CodexConfigPath
                : null,
            InstalledAt = DateTimeOffset.UtcNow
        };
        SetupStateStore.Save(preliminary);

        report("执行 Anode setup（启用 RDP 主机与子会话）...");
        var setupResult = await RunProcessAsync(
            anode.ExecutablePath,
            new[] { "setup" },
            AnodeSetupTimeout,
            cancellationToken).ConfigureAwait(false);
        if (setupResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"anode setup 失败（退出码 {setupResult.ExitCode}）。可运行 uninstall 回滚已记录的设置。{FormatOutput(setupResult)}");
        }

        if (SetupInspector.ReadRdpDenied() != 0)
        {
            throw new InvalidOperationException("anode setup 后远程桌面主机的注册表状态仍未启用。");
        }

        if (!WindowsNative.TryGetChildSessionsEnabled(out var childSessionsEnabled) || !childSessionsEnabled)
        {
            throw new InvalidOperationException("anode setup 后 Windows 子会话仍不可用。");
        }

        string? codexBackup = null;
        if (options.SkipCodex)
        {
            report("已按参数跳过 Codex 接入。");
        }
        else if (!File.Exists(SetupPaths.CodexConfigPath))
        {
            report("未检测到 Codex 配置，已跳过客户端接入。");
        }
        else
        {
            report("写入 Codex MCP 接入配置（保留 CC Switch 与模型配置）...");
            var result = CodexConfigManager.Install(
                SetupPaths.CodexConfigPath,
                installedBridge,
                options.AgentId,
                options.AgentName);
            codexBackup = result.BackupPath;
            report($"Codex 配置已更新；备份：{result.BackupPath}");
        }

        if (!options.SkipAutostart)
        {
            WriteAutostart(installedDesktop);
            report("已添加当前用户登录自启动。");
        }

        var finalState = preliminary with
        {
            CodexConfigPath = File.Exists(SetupPaths.CodexConfigPath) ? SetupPaths.CodexConfigPath : null,
            CodexBackupPath = codexBackup,
            AutostartAdded = !options.SkipAutostart
        };
        SetupStateStore.Save(finalState);
        report($"安装完成：{installRoot}");
    }

    public static async Task UninstallAsync(
        SetupOptions options,
        Action<string> report,
        CancellationToken cancellationToken)
    {
        if (!SetupInspector.IsAdministrator())
        {
            throw new UnauthorizedAccessException("卸载需要管理员权限。");
        }

        var state = SetupStateStore.Load()
            ?? throw new InvalidOperationException("未找到 AgentOrb 安装状态，已拒绝修改系统设置。");
        EnsureInstalledProgramsNotRunning(state.InstallRoot);

        var anode = SetupInspector.FindAnodeExecutable();
        if (anode is not null)
        {
            report("请求 Anode 关闭当前 seat...");
            var quit = await RunProcessAsync(
                anode,
                new[] { "quit" },
                AnodeQuitTimeout,
                cancellationToken).ConfigureAwait(false);
            if (quit.ExitCode != 0)
            {
                report("Anode quit 未正常完成，继续恢复安装前设置。");
            }

            report("执行 Anode 官方回滚...");
            var undo = await RunProcessAsync(
                anode,
                new[] { "setup", "--undo" },
                AnodeSetupTimeout,
                cancellationToken).ConfigureAwait(false);
            if (undo.ExitCode != 0)
            {
                report("anode setup --undo 未正常完成，将按安装前快照恢复。");
            }
        }

        if (state.CodexConfigPath is not null && File.Exists(state.CodexConfigPath))
        {
            if (CodexConfigManager.Uninstall(state.CodexConfigPath))
            {
                report("已移除 Codex 中的 AgentOrb MCP 配置。");
            }
        }

        RestoreAutostart(state.PreviousAutostartValue);
        if (state.PreviousChildSessions is bool previousChildSessions)
        {
            WindowsNative.EnableChildSessions(previousChildSessions);
        }

        if (state.PreviousRdpDeny is int previousRdpDeny)
        {
            SetRdpDeny(previousRdpDeny);
        }

        TryDeleteInstalledFiles(state);
        SetupStateStore.Delete();
        report("卸载与设置恢复完成。");
    }

    public static void PrintHelp()
    {
        Console.WriteLine(
            """
            AgentOrb.Setup - 双会话隔离运行时安装器

            check     只读检查 Windows 能力、Anode、运行时和 Codex 配置
            install   安装并配置 AgentOrb、Anode、子会话和 Codex MCP
            uninstall 关闭 seat 并恢复安装前设置

            install 选项：
              --accept-rdp-change  必填，确认启用远程桌面主机和子会话
              --anode-archive PATH 使用本地 Anode x64 ZIP（默认下载 v0.11.4）
              --anode-sha256 HASH  覆盖固定 SHA-256（仅用于可信离线包）
              --skip-codex         不修改 Codex 配置
              --skip-autostart     不添加当前用户登录启动
              --no-start           安装后不启动 AgentOrb
              --agent-id ID        MCP Agent ID，默认 codex
              --agent-name NAME    MCP Agent 名称，默认 Codex

            check 选项：
              --json               输出机器可读结果
            """);
    }

    private static void CopyRuntimeFile(string fileName, string installRoot)
    {
        var source = Path.Combine(SetupPaths.PackageRoot, fileName);
        var destination = Path.Combine(installRoot, fileName);
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"安装包缺少 {fileName}。", source);
        }

        File.Copy(source, destination, overwrite: true);
    }

    private static void EnsureInstalledProgramsNotRunning(string installRoot)
    {
        foreach (var processName in new[] { "AgentOrb", "AgentOrb.Bridge" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    string? path = null;
                    try
                    {
                        path = process.MainModule?.FileName;
                    }
                    catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                    {
                        // Unable to inspect another user's process; conservatively block only by name.
                    }

                    if (path is null || SetupPaths.IsWithin(path, installRoot))
                    {
                        throw new InvalidOperationException("AgentOrb 或 Bridge 仍在运行，请先退出后再安装/卸载。");
                    }
                }
            }
        }
    }

    private static bool IsOurAutostartValue(string? value, string installedDesktop)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return string.Equals(
            value.Trim().Trim('"'),
            Path.GetFullPath(installedDesktop),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadAutostartValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SetupPaths.AutostartRunKey);
        return key?.GetValue(AutostartValueName) as string;
    }

    private static void WriteAutostart(string executable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SetupPaths.AutostartRunKey, writable: true)
            ?? throw new UnauthorizedAccessException("无法写入当前用户启动项。");
        key.SetValue(AutostartValueName, $"\"{executable}\"", RegistryValueKind.String);
    }

    private static void RestoreAutostart(string? previousValue)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SetupPaths.AutostartRunKey, writable: true);
        if (key is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(previousValue))
        {
            key.DeleteValue(AutostartValueName, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(AutostartValueName, previousValue, RegistryValueKind.String);
        }
    }

    private static void SetRdpDeny(int value)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Terminal Server",
            writable: true) ?? throw new UnauthorizedAccessException("无法写入远程桌面主机设置。");
        key.SetValue("fDenyTSConnections", value, RegistryValueKind.DWord);
    }

    private static void TryDeleteInstalledFiles(SetupState state)
    {
        if (!SetupPaths.IsSafeInstallRoot(state.InstallRoot))
        {
            return;
        }

        foreach (var fileName in new[] { "AgentOrb.exe", "AgentOrb.Bridge.exe", "AgentOrb.Setup.exe" })
        {
            var path = Path.Combine(state.InstallRoot, fileName);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // A running installer may lock its own file; leave it for manual removal.
            }
        }

        try
        {
            if (Directory.Exists(state.InstallRoot)
                && !Directory.EnumerateFileSystemEntries(state.InstallRoot).Any())
            {
                Directory.Delete(state.InstallRoot);
            }
        }
        catch (IOException)
        {
            // The install directory may still contain user files.
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"无法启动 {Path.GetFileName(executable)}。");
            }
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"无法启动 {Path.GetFileName(executable)}。", exception);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"{Path.GetFileName(executable)} 超时（{timeout.TotalSeconds:0} 秒）。");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));
    }

    private static string FormatOutput(ProcessResult result)
    {
        var combined = (result.StandardError + Environment.NewLine + result.StandardOutput).Trim();
        if (combined.Length == 0)
        {
            return string.Empty;
        }

        if (combined.Length > 1200)
        {
            combined = combined[..1200] + "…";
        }

        return Environment.NewLine + combined;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Process may have exited concurrently.
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
