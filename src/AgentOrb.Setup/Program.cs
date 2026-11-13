using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AgentOrb.Setup;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // Encoding is best-effort in redirected consoles.
        }

        if (args.Length == 0 && !Console.IsInputRedirected)
        {
            Console.WriteLine("AgentOrb Setup");
            Console.WriteLine("安装将启用 Windows 远程桌面主机和子会话，并在首次登录时可能需要 Windows 密码。");
            Console.WriteLine("不会改动 CC Switch 或模型密钥；Codex 配置会先备份。");
            Console.Write("输入 YES 继续安装，直接回车只执行检查：");
            var answer = Console.ReadLine();
            if (string.Equals(answer, "YES", StringComparison.Ordinal))
            {
                args = new[] { "install", "--accept-rdp-change" };
            }
        }

        SetupOptions options;
        try
        {
            options = SetupOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine("执行 AgentOrb.Setup.exe help 查看用法。");
            return 2;
        }

        if (options.Command == SetupCommand.Help)
        {
            SetupEngine.PrintHelp();
            return 0;
        }

        if (options.Command == SetupCommand.Check)
        {
            return RunCheck(options);
        }

        if (!SetupInspector.IsAdministrator())
        {
            if (options.IsElevated)
            {
                Console.Error.WriteLine("提权进程未获得管理员权限，安装已中止。");
                return 1;
            }

            var exitCode = Elevation.Relaunch(args);
            if (exitCode == 0 && options.Command == SetupCommand.Install && !options.NoStart)
            {
                TryStartInstalled(options);
            }

            return exitCode;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            if (options.Command == SetupCommand.Install)
            {
                await SetupEngine.InstallAsync(options, Console.WriteLine, cancellation.Token).ConfigureAwait(false);
                if (options.IsElevated)
                {
                    Console.WriteLine("安装完成；父进程将在提权进程退出后启动 AgentOrb。");
                }
                else
                {
                    Console.WriteLine("安装完成。重新登录后会自动启动，或手动运行安装目录中的 AgentOrb.exe。");
                }
            }
            else
            {
                await SetupEngine.UninstallAsync(options, Console.WriteLine, cancellation.Token).ConfigureAwait(false);
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("操作已取消。若已修改系统设置，可重新执行 uninstall 回滚。");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"安装器失败（{exception.GetType().Name}）：{exception.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static int RunCheck(SetupOptions options)
    {
        var inspection = SetupInspector.Inspect(options.InstallRoot);
        if (options.Json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
                inspection,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine("AgentOrb Setup Check");
            Console.WriteLine($"系统：{inspection.OsDescription} (build {inspection.BuildNumber}, {inspection.Edition})");
            Console.WriteLine($"远程桌面主机：{(inspection.RdpHostEnabled ? "已启用" : "未启用")}");
            Console.WriteLine($"Windows 子会话：{(inspection.ChildSessionsEnabled ? "已启用" : "未启用")}");
            Console.WriteLine($"Anode：{inspection.AnodePath ?? "未安装"}");
            Console.WriteLine($"安装包运行时：{(inspection.RuntimeSourcePresent ? "完整" : "缺失")}");
            Console.WriteLine($"已安装运行时：{(inspection.RuntimeInstalled ? "存在" : "不存在")}");
            Console.WriteLine($"Codex 配置：{(inspection.CodexConfigPresent ? "存在" : "未检测到")}");
            Console.WriteLine($"管理员权限：{(inspection.IsAdmin ? "是" : "否（安装时提权）")}");

            if (inspection.BlockingIssues.Count > 0)
            {
                Console.WriteLine("阻塞项：");
                foreach (var issue in inspection.BlockingIssues)
                {
                    Console.WriteLine($"  [BLOCKED] {issue}");
                }
            }

            if (inspection.Warnings.Count > 0)
            {
                Console.WriteLine("提示：");
                foreach (var warning in inspection.Warnings)
                {
                    Console.WriteLine($"  [WARN] {warning}");
                }
            }

            Console.WriteLine(inspection.CanInstall ? "结论：可以安装。" : "结论：当前环境不可安装。");
        }

        return inspection.CanInstall ? 0 : 1;
    }

    private static void TryStartInstalled(SetupOptions options)
    {
        var executable = SetupPaths.AgentOrbExecutable(options.InstallRoot);
        if (!File.Exists(executable))
        {
            Console.Error.WriteLine("安装完成，但未找到 AgentOrb.exe，无法自动启动。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = options.InstallRoot,
                UseShellExecute = true
            });
        }
        catch (Win32Exception exception)
        {
            Console.Error.WriteLine($"安装完成，但自动启动失败：{exception.Message}");
        }
    }
}

internal static class Elevation
{
    private const int ErrorCancelled = 1223;

    public static int Relaunch(string[] args)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定安装器自身路径。");
        var elevatedArguments = args
            .Concat(new[] { "--elevated" })
            .Select(QuoteArgument);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = string.Join(' ', elevatedArguments),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory
        };

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动提权安装进程。");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            Console.Error.WriteLine("用户取消了管理员授权，未修改系统。");
            return 3;
        }
        catch (Win32Exception exception)
        {
            Console.Error.WriteLine($"无法请求管理员权限：{exception.Message}");
            return 1;
        }
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        if (!argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var builder = new StringBuilder(argument.Length + 8);
        builder.Append('"');
        var backslashCount = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashCount * 2 + 1);
                builder.Append('"');
            }
            else
            {
                builder.Append('\\', backslashCount);
                builder.Append(character);
            }

            backslashCount = 0;
        }

        builder.Append('\\', backslashCount * 2);
        builder.Append('"');
        return builder.ToString();
    }
}
