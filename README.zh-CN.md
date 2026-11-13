# AgentOrb

**给 AI Agent 一个受监督的独立 Windows 桌面。**

AgentOrb 让 AI Agent 拥有自己的 Windows 子会话来操作——独立的屏幕、鼠标和键盘焦点——你的桌面不受打扰。屏幕顶部的小岛会主动汇报 Agent 正在做什么，你不需要盯着它。

[English](README.md)

---

## 为什么需要它

会操作电脑的 Agent 需要屏幕、光标和前台窗口。和它共用你的桌面很痛苦：写字写到一半光标被挪走、窗口频繁被抢焦点、也看不出它到底改了什么。

AgentOrb 把两者分开：Agent 通过 [Anode](https://github.com/skulitom/Anode) 驱动一个 Windows **子会话**（seat），你的桌面保留自己的光标、键盘焦点和窗口。Agent 的点击和输入不会进入你的会话。

## 功能特性

- **灵动岛状态层**——顶部居中的胶囊，Agent 开始动作时自动展开，空闲时自动收敛；显示 Agent、动作、状态、用时和来源，且从不抢你窗口的焦点。
- **真实输入隔离**——已实测：在 seat 内点击并输入 40 个字符，用户桌面的光标位置和前台窗口保持不变。
- **MCP 优先**——Agent 通过 `AgentOrb.Bridge.exe`（MCP stdio 服务器）使用 41 个桌面工具：点击、输入、截图、窗口、进程、无障碍读取、任务、手柄等。
- **一键查看与接管**——点击灵动岛打开 seat 查看器，再次点击隐藏并把焦点交还给你。
- **默认安全**——Bridge 走机器级命名管道，由服务端内核 ACL 限制同用户；状态事件不携带参数、返回内容或密钥；MCP 调用 10 分钟超时；桌面租约让多个 Agent 排队而非争抢同一只鼠标。
- **离线友好安装器**——下载固定版本 Anode（v0.11.4）并校验 SHA-256；Codex 配置只写入一个可逆的受管理块。

## 系统要求

- Windows 10 / 11 **专业版、企业版或教育版**，x64。家庭版不支持所依赖的子会话能力。
- 安装时需要**一次**管理员权限：开启远程桌面监听器和子会话属于系统级变更。
- 安装器会从 Anode 的 GitHub Releases 下载固定版本并校验 SHA-256。

> AgentOrb **不是安全沙箱**。seat 以你的身份登录，共享你的文件、账户和网络。你接入的每个 MCP 客户端都应被视为"可以使用你桌面"的程序。

## 安装

1. 下载 `AgentOrb-Setup-vX.Y.Z-win-x64.zip` 并解压。
2. 在**普通权限**终端执行：

   ```bat
   AgentOrb.Setup.exe install --accept-rdp-change
   ```

   `--accept-rdp-change` 必须显式给出：安装器会开启远程桌面主机与子会话，并记录变更前的值，供卸载时恢复。

3. 随时检查状态：

   ```bat
   AgentOrb.Setup.exe check
   ```

4. `AgentOrb.Setup.exe uninstall` 会移除运行时、Codex 管理块和自启动项，并还原 RDP/子会话设置。

安装位置为 `%LOCALAPPDATA%\Programs\AgentOrb`，并写入当前用户自启动项。可从开始菜单、桌面快捷方式或托盘启动。

## 配合 Codex 使用

安装器会向 `%USERPROFILE%\.codex\config.toml` 追加受管理块，不改动任何模型/供应商配置：

```toml
# >>> AgentOrb managed block >>>
[mcp_servers.agentorb]
command = "C:\\Users\\<你>\\AppData\\Local\\Programs\\AgentOrb\\AgentOrb.Bridge.exe"
# <<< AgentOrb managed block <<<
```

重启 Codex 客户端，然后直接派活：

> 在 Agent 桌面里打开记事本，输入"hello from the seat"，然后截图。

你会看到：Codex 请求工具授权 → 灵动岛展开并显示动作、Agent 名称和计时 → 点击和输入全部落在 seat，而不是你的桌面。

其他支持 MCP 的 Agent 同理，把 MCP 服务器指向 `AgentOrb.Bridge.exe` 即可；不支持 MCP 的 Agent 可以通过 Anode CLI 操作 seat，灵动岛仍会通过同一管道汇报。

## 灵动岛交互

| 操作 | 效果 |
|---|---|
| 不操作 | 自动汇报 Agent 动作，完成后收敛 |
| 单击 | 打开 seat 查看器（取得控制权） |
| 再次单击 | 隐藏查看器并恢复原前台窗口 |
| 右键 | 启动桌面 / 查看桌面 / 紧急停止 / 退出 |

灵动岛窗口本身带 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`，不抢焦点；查看器打开时刻意取得焦点，因为那是你接管的方式。

## 架构

```
Agent (Codex / Claude …)  ── MCP stdio ──  AgentOrb.Bridge.exe
                                                   │ 命名管道
                                                   │ \\.\pipe\agentorb-status-v1
                                          AgentOrb.Desktop（灵动岛 UI）
                                                   │ CLI
                                            Anode（seat 子会话 + RDP）
```

| 项目 | 职责 |
|---|---|
| `AgentOrb.Core` | 状态契约、JSON 格式、工具名映射 |
| `AgentOrb.Anode` | Anode CLI 封装（status / show / hide / start / stop） |
| `AgentOrb.Desktop` | WPF 灵动岛、托盘、查看器切换、状态管道服务端 |
| `AgentOrb.Bridge` | MCP stdio 服务端、管道客户端、调用跟踪与脱敏 |
| `AgentOrb.Setup` | 安装器：RDP/子会话、Anode 供给、Codex 配置 |
| `tests/AgentOrb.Tests` | 24 项零依赖测试（管道、MCP、安装器逻辑） |

## 从源码构建

需要 Windows 上的 .NET 8 SDK。

```bat
dotnet build AgentOrb.sln -c Release
dotnet run --project tests\AgentOrb.Tests\AgentOrb.Tests.csproj -c Release
```

测试项目不依赖任何测试框架，是纯控制台运行器，因此整个构建零 NuGet 依赖。

## 当前状态

能用但早期：v0.1.10 已在 Windows 11 + Codex 桌面版实测通过。
尚未完成：代码签名（会触发 SmartScreen）、自动更新、崩溃恢复、多 seat 并发。
开源准备清单见 `docs/opensource-readiness.md`。

## 许可证

MIT，见 [LICENSE](LICENSE)。选择理由：与运行时依赖 Anode 保持一致、降低采用门槛、避免为这种规模的桌面自动化工具引入专利条款复杂度。
第三方声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
