namespace AgentOrb.Setup;

internal enum SetupCommand
{
    Help,
    Check,
    Install,
    Uninstall
}

internal sealed record SetupOptions
{
    public SetupCommand Command { get; init; }
    public bool Json { get; init; }
    public string? AnodeArchive { get; init; }
    public string? AnodeSha256 { get; init; }
    public bool SkipCodex { get; init; }
    public bool SkipAutostart { get; init; }
    public bool NoStart { get; init; }
    public bool AcceptRdpChange { get; init; }
    public bool IsElevated { get; init; }
    public string InstallRoot { get; init; } = SetupPaths.DefaultInstallRoot;
    public string AgentId { get; init; } = "codex";
    public string AgentName { get; init; } = "Codex";

    public static SetupOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new SetupOptions { Command = SetupCommand.Check };
        }

        var command = args[0].ToLowerInvariant() switch
        {
            "check" or "doctor" => SetupCommand.Check,
            "install" => SetupCommand.Install,
            "uninstall" => SetupCommand.Uninstall,
            "help" or "--help" or "-h" => SetupCommand.Help,
            _ => throw new ArgumentException("未知命令。可用命令：check、install、uninstall、help。")
        };

        var options = new SetupOptions { Command = command };
        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--json":
                    options = options with { Json = true };
                    break;
                case "--anode-archive":
                    options = options with { AnodeArchive = ReadPath(args, ref index, "--anode-archive") };
                    break;
                case "--anode-sha256":
                    options = options with { AnodeSha256 = ReadSha256(args, ref index) };
                    break;
                case "--install-root":
                    options = options with { InstallRoot = ReadPath(args, ref index, "--install-root") };
                    break;
                case "--agent-id":
                    options = options with { AgentId = ReadValue(args, ref index, "--agent-id") };
                    break;
                case "--agent-name":
                    options = options with { AgentName = ReadValue(args, ref index, "--agent-name") };
                    break;
                case "--skip-codex":
                    options = options with { SkipCodex = true };
                    break;
                case "--skip-autostart":
                    options = options with { SkipAutostart = true };
                    break;
                case "--no-start":
                    options = options with { NoStart = true };
                    break;
                case "--accept-rdp-change":
                    options = options with { AcceptRdpChange = true };
                    break;
                case "--elevated":
                    options = options with { IsElevated = true };
                    break;
                default:
                    throw new ArgumentException($"未知参数：{args[index]}");
            }
        }

        if (!Enum.IsDefined(options.Command))
        {
            throw new ArgumentException("安装命令无效。");
        }

        if (options.Command == SetupCommand.Install && !options.AcceptRdpChange)
        {
            throw new ArgumentException(
                "安装会启用远程桌面和 Windows 子会话。确认风险后重新执行并添加 --accept-rdp-change。");
        }

        if (options.Command != SetupCommand.Check && options.Json)
        {
            throw new ArgumentException("--json 只支持 check 命令。");
        }

        if (!SetupPaths.IsSafeInstallRoot(options.InstallRoot))
        {
            throw new ArgumentException("安装目录必须是当前用户配置目录下的 AgentOrb 专用子目录。");
        }

        if (string.IsNullOrWhiteSpace(options.AgentId) || options.AgentId.Length > 120)
        {
            throw new ArgumentException("Agent ID 不能为空或过长。");
        }

        if (string.IsNullOrWhiteSpace(options.AgentName) || options.AgentName.Length > 120)
        {
            throw new ArgumentException("Agent 名称不能为空或过长。");
        }

        return options;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"{option} 缺少有效值。");
        }

        return args[index];
    }

    private static string ReadPath(string[] args, ref int index, string option)
    {
        var value = ReadValue(args, ref index, option);
        return Path.GetFullPath(value);
    }

    private static string ReadSha256(string[] args, ref int index)
    {
        var value = ReadValue(args, ref index, "--anode-sha256").Trim();
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("--anode-sha256 必须是 64 位十六进制 SHA-256。");
        }

        return value.ToLowerInvariant();
    }
}
