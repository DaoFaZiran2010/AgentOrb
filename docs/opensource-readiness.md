# AgentOrb 开源就绪评估与准备清单

> **状态更新（2026-11-13）**：P0 中"仓库整理 / 许可证 / 文档最小集 / 敏感信息清理"已落地——
> `git init`（main 分支，首个提交 52 个文件）、`.gitignore` 已排除 `artifacts/ bin/ obj/ .tools/`、
> MIT `LICENSE`、`README.md` + `README.zh-CN.md`、`CONTRIBUTING.md`、`CODE_OF_CONDUCT.md`、
> `SECURITY.md`、`THIRD-PARTY-NOTICES.md` 均已提交。剩余前置项：GitHub 远程仓库与首个 push、
> CI/Release 工作流、代码签名。

评估日期：2026-11-13 · 评估对象：v0.1.8（90 个 C# 源文件，6 个项目）

---

## 一、总体结论

**适合开源，但当前处于"本地项目"状态，未达到公开仓库标准。**

代码本体非常干净：零外部 NuGet 依赖、源码无硬编码密钥/个人路径/凭据、架构分层清晰（Core / Anode / Desktop / Bridge / Setup / Tests）、24 项自动化测试通过。真正阻断开源的不是代码，而是工程化配套：

- **没有 git 仓库**（最大的阻断项，历史变更全部只在本地）
- 没有 README / LICENSE / CONTRIBUTING 等任何社区文档
- `artifacts/` 目录 3.6 GB，含历史安装包、日志、本机桌面截图、注册表/ACL 备份
- 发布流程纯手工（publish → 复制 → Compress-Archive → 手抄哈希），无 CI、无签名

建议：完成下方 **P0 清单** 后可先开 **Private 仓库** 试跑 1–2 个迭代，再转 Public。

---

## 二、现状盘点

| 维度    | 现状                                      | 评级  |
| ----- | --------------------------------------- | --- |
| 源代码质量 | 0 错误 0 警告；24/24 测试；分层清晰                 | 优   |
| 第三方依赖 | 零 NuGet 包，仅 .NET 8 BCL；运行时依赖 Anode（MIT） | 优   |
| 敏感信息  | 源码无密钥/凭据；但产物目录含本机截图、ACL 备份、用户路径         | 中风险 |
| 仓库管理  | 无 git、无 .gitignore、无 CI                 | 缺失  |
| 文档    | 无 README、无使用/安装/贡献文档                    | 缺失  |
| 许可证   | 未选择；Anode 部分为 MIT（已确认，可再分发）             | 缺失  |
| 发布流程  | 手工打包、未签名（SmartScreen 会拦截）               | 初级  |
| 版本管理  | Directory.Build.props 集中管理，版本号统一        | 良好  |

---

## 三、可执行开源准备清单

### P0 —— 公开前的阻断项（必须先做）

#### 1. 代码仓库整理

- [ ] `git init` 并建立仓库骨架；确认本地历史无法追溯的部分（v0.1.0–v0.1.8 无 VCS 历史）——**决策点：接受"首个 commit 即当前状态"，还是补写历史？建议接受现状，在 CHANGELOG 中记录版本沿革。**
- [ ] 编写 `.gitignore`（至少覆盖）：
  ```
  bin/ obj/ artifacts/ .tools/ *.user *.suo .vs/
  ```
- [ ] 将 `artifacts/`（3.6 GB：历史 zip、安装日志、acceptance 截图、`backups/rdp-pre-repair` 注册表与 ACL 导出）整体移出仓库目录，改为本地归档（如 `../AgentOrb-archive/`），**绝不提交**。
- [ ] `src/` 内 364 MB 主要来自 bin/obj 构建产物，靠 .gitignore 排除即可；可额外执行一次清理脚本验证仓库体积 < 5 MB。
- [ ] 移除或归档 `obsolete/` 旧版本目录。

#### 2. 敏感信息清理

- [ ] 源码全量复核（当前已确认干净）：无 `C:\Users\...` 硬编码路径、无 token/密码（`grep -rn "C:\\\\" src` 零命中）。  
  |  |  |  
  | --- | --- |
- [ ] `artifacts/acceptance/*.bmp|png`：含本机桌面截图（可见 WorkBuddy 窗口标题等），**留在本地，不进仓库**；未来截图改存 CI 产物。
- [ ] `artifacts/backups/rdp-pre-repair/`：MachineKeys ACL 导出、RDP-TCP XML 含本机 SID/证书指纹，**仅本地留存**。
- [ ] `artifacts/acceptance/*.py` 探测脚本含本机绝对路径（如 `C:\Users\Administrator\AppData\...`），入库前参数化（读环境变量 `LOCALAPPDATA`）。
- [ ] 确认 `~/.codex/config.toml` 备份（含 CC Switch 配置）**不在仓库内**——当前确认不在，保持。
- [ ] 全局做一次 `git add` 前的 `gitleaks` / `trufflehog` 扫描，形成扫描记录。

#### 3. 依赖与许可证合规

- [ ] 为本项目选择许可证。**建议 MIT**（与 Anode 一致、降低用户理解成本）或 Apache-2.0（含专利授权条款）。写入 `LICENSE` 并在每个 csproj 加 `<PackageLicenseExpression>`。
- [ ] Anode（上游运行时依赖，MIT，© 2026 skulitom）：
  - 合规要点：再分发需保留版权声明。
  - 当前安装器是**运行时下载**固定 v0.11.4 并校验 SHA-256（不合体分发）——此方式最干净，保持。
  - 若未来在 Release 资产里直接附带 Anode zip，需在 Release 说明和 `THIRD-PARTY-NOTICES.md` 中标注其 MIT 版权。
- [ ] 新增 `THIRD-PARTY-NOTICES.md`：列明 Anode（MIT）与 .NET 运行时（MIT）两项。
- [ ] `AgentOrb.Setup` 中固定的 Anode v0.11.4 + SHA-256 写入 `docs/sbom.md`（软件物料清单），随版本更新。

#### 4. 文档与贡献指南（最小集）

- [ ] `README.md`（中英文至少英文）：是什么、灵动岛截图/GIF、30 秒上手、系统要求（**Windows 10/11 Pro+**）、架构图、免责声明。
- [ ] `docs/ARCHITECTURE.md`：五个项目的职责与数据流（Bridge↔Desktop 命名管道、Desktop↔Anode CLI、child session 原理）。
- [ ] `docs/SECURITY.md`：**必须写**——威胁模型（本地单用户、MCP 输入即在本机桌面执行、非沙箱）、漏洞上报渠道、已知边界（见风险清单 R4）。
- [ ] `CONTRIBUTING.md`：本地构建指引（项目自带 `dotnet` 于 `.tools/`，贡献者需知 bootstrap 方式）、测试运行方式（`dotnet run --project tests/AgentOrb.Tests`）、提交流程。
- [ ] `CODE_OF_CONDUCT.md`（Contributor Covenant 2.1）。
- [ ] `CHANGELOG.md`：回溯记录 v0.1.0–v0.1.8 的演进（可从本地记忆/发布包整理）。

#### 5. 版本与发布流程

- [ ] 确立语义化版本规则：当前 0.1.x 阶段 = "0.x 时期，次版本可含破坏性变更"，1.0 门槛单独定义（建议：代码签名 + 自动更新 + 崩溃恢复后再发 1.0）。
- [ ] GitHub Actions 工作流（`ci.yml`）：PR/主干触发 `dotnet build -c Release` + 24 项测试，构建产物不进仓库。
- [ ] GitHub Actions 工作流（`release.yml`）：打 `v*` tag 自动 publish 三个单文件 exe、生成 zip、计算 SHA-256 并写入 Release Notes——替代当前纯手工流程。
- [ ] 发布资产命名与目录结构规范化：`AgentOrb-v{ver}-win-x64.zip` / `AgentOrb-Setup-v{ver}-win-x64.zip`（保持现有命名）。
- [ ] 建立 `docs/release-checklist.md`：发版前逐项核对（测试、哈希、状态文件、Codex 配置回归）。

### P1 —— 公开后 1–2 个迭代内完成

- [ ] 代码签名证书（OV 代码签名 + 时间戳），消除 SmartScreen 拦截； EV 证书可立即建立信誉，按需选择。
- [ ] Issue/PR 模板（bug、功能、安全三类；安全类引导至 SECURITY.md 私密渠道）。
- [ ] `docs/USAGE.md`：完整使用手册（灵动岛交互、查看器控制、租约机制、Codex/Claude 接入示例）。
- [ ] `docs/TROUBLESHOOTING.md`：已知的 RDP 0x80070005、Anode 管道 ACL、seat 停止等排障经验（本机已有实战记录可整理）。
- [ ] 英文界面文案核对（当前 UI 中文为主，开源建议 i18n 或至少 README 双语）。
- [ ] 商标检索："AgentOrb" 名称与图形标识在目标市场（至少中国、美国）做商标检索，确认无冲突后注册。

### P2 —— 成熟期

- [ ] 自动更新机制（建议 later：后台下载 + 用户确认，签名后是前提）。
- [ ] 崩溃上报（需隐私评估，默认关、显式开启）。
- [ ] 多语言 UI。
- [ ] 贡献者增长配套：good-first-issue 标记、路线图 ROADMAP.md。

---

## 四、风险清单与处理建议

| #  | 风险                          | 级别 | 说明与处理                                                                                                                                                                                  |
| -- | --------------------------- | -- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| R1 | **Windows 子会话/RDP 的许可灰色地带** | 高  | child session + 环回 RDP 依赖 Windows 的 ChildSessions 功能（非公开承诺的正式特性），且启用 RDP 主机是系统级变更。处理：`README` 与安装器明确声明"需要 Windows 10/11 Pro 及以上、自行承担许可责任"；保留 `--accept-rdp-change` 显式确认；卸载时完整回滚（现有机制）。 |
| R2 | **未签名二进制触发 SmartScreen**    | 中  | 用户下载即被拦截，损害第一印象。处理：P1 代码签名；短期在 README 写明"更多 → 仍要运行"的指引。                                                                                                                                |
| R3 | **隐私数据随仓库泄露**               | 中  | 本机截图/ACL 备份/SID 已在产物目录。处理：P0-2 全部项 + 首次 `git push` 前做一次全量 `gitleaks` 扫描留档。                                                                                                             |
| R4 | **MCP 无沙箱执行的安全边界**          | 中  | 任何能连接 Bridge 的 MCP 客户端都能在用户桌面执行输入；当前缓解：本地命名管道 + 单用户 + 10 分钟 TTL + 不记录参数。处理：SECURITY.md 明确威胁模型；未来可加"客户端白名单/操作确认"选项。                                                                     |
| R5 | **Anode 上游单点依赖**            | 中  | 核心桌面能力完全依赖 Anode v0.11.4（API/CLI 行为、许可证、维护状态）。缓解：已固定版本+哈希；建议关注上游 release，建立升级回归流程；注意 Anode 为个人维护项目（UK sole trader），存在断供风险，必要时 fork。                                                    |
| R6 | **游戏自动化合规**                 | 低  | 用户可能将其用于游戏，触发游戏厂商 ToS/反作弊。处理：README 声明"请遵守目标软件条款"，不提供规避反作弊的功能。                                                                                                                         |
| R7 | **RDP 暴露面**                 | 低  | 安装器只启用监听器、未放行防火墙（当前实测无启用规则）。处理：文档写明；未来可加"仅环回"加固选项。                                                                                                                                     |

---

## 五、建议的开源节奏

1. **本周（P0）**：git init + .gitignore + 移出 artifacts + LICENSE/README/SECURITY 最小集 → 推 **Private** 仓库。
2. **1–2 迭代（P1）**：CI/release 自动化、代码签名、使用文档 → 仓库转 Public，发 v0.2.0 作为"开源首秀"版本。
3. **持续（P2）**：按上表推进，1.0 标准：签名 + 自动更新 + 崩溃恢复 + 多 Agent 租约实测。
