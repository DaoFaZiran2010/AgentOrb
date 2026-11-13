# Changelog

All notable changes to AgentOrb are documented in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
semantic versioning. Releases before 1.0.0 are early development and may change interfaces.

## [Unreleased]

- GitHub Actions CI (build + 24 tests on Windows) and tag-triggered release workflow that
  publishes self-contained win-x64 packages with SHA-256 checksums.

## [0.1.10] - 2026-11-13

- Machine-wide single-instance lock (`Global\`): prevents a second AgentOrb from starting inside
  the Agent seat, which previously split named-pipe status events between two islands.
- Non-console duplicate instances now exit silently instead of leaving a dialog in the seat.

## [0.1.9] - 2026-11-13

- Fixed the island permanently showing "waiting for bridge": removed the client-side
  `CurrentUserOnly` pipe option, which fails across sessions/containers (the server's kernel ACL
  already enforces same-user access).
- Bridge pipe connect timeout raised to 5 s; one-time stderr diagnostic on repeated failure.

## [0.1.8] - 2026-11-13

- Replaced the side dot with a top-centred **Dynamic Island** status surface: compact capsule
  when idle, auto-expanding card on agent activity (agent, action, state, elapsed time, source),
  auto-collapse after idle.
- Island window is `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`; clicking it toggles the seat viewer
  and restores the previous foreground window on hide.

## [0.1.7] - 2026-11-13

- Installer state snapshot fix: upgrading no longer records AgentOrb's own autostart entry as
  the pre-install value, so `uninstall` restores the correct previous state.

## [0.1.6] - 2026-11-13

- Viewer show/hide keeps or restores the user's foreground window correctly.
- Removed an elevation check that misfired under UAC-filtered tokens.

## [0.1.5] - 2026-11-13

- Island/bubble no longer steals focus; foreground window is saved and restored across viewer
  toggles.

## [0.1.4] - 2026-11-13

- New `AgentOrb.Setup`: read-only `check`, consented `install`/`uninstall`, RDP and child-session
  provisioning, pinned Anode v0.11.4 download with SHA-256 verification, reversible managed
  Codex MCP config block, per-user autostart.

## [0.1.3] - 2026-11-13

- Status pipe accepts up to 8 concurrent Bridge clients; lifecycle status only on first connect
  and last disconnect.
- Pending MCP calls expire after 10 minutes and are cleaned up; arguments, results and secrets
  are never published.

## [0.1.2] - 2026-11-13

- Custom dark tray/context menus; first working end-to-end status reporting through the bridge.

## [0.1.0] - 2026-11-13

- Initial prototype: WPF status dot, tray integration, MCP stdio bridge, Anode seat wrapper.
