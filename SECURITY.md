# Security policy

## Supported versions

| Version | Supported |
|---|---|
| 0.1.x | Yes (latest patch only) |

## Reporting a vulnerability

Please **do not** open a public issue. Use GitHub's private vulnerability reporting
(Security tab → "Report a vulnerability") on this repository. If you cannot use that channel,
contact a maintainer directly on GitHub and ask for a private channel.

Include: affected version, reproduction steps, the impact you expect, and whether the issue
requires a local attacker, a malicious MCP client, or physical access. We aim to acknowledge
reports within 7 days.

## Threat model — read this before deploying

AgentOrb gives an MCP client control of a real Windows desktop. That is the product, not a
defect. Be explicit about what that means:

**In scope, by design**

- Any MCP client that connects to `AgentOrb.Bridge.exe` can click, type, launch programs,
  read screenshots and enumerate windows **inside the seat**.
- The seat runs **as your Windows user** and shares your files, credentials, browser profiles
  and network. It is an input/attention boundary, **not a security sandbox**.
- Granting a tool call is equivalent to letting that agent use your desktop.

**Mitigations that exist today**

- The status pipe is a machine-local named pipe whose server ACL restricts access to the
  current user (kernel enforcement, not a client-side check).
- The bridge publishes fixed status strings only: no tool arguments, no results, no secrets.
- Pending MCP calls expire after 10 minutes and are cleaned up.
- A desktop lease serialises agents so two clients cannot interleave pointer/keyboard input.
- Installation requires an explicit `--accept-rdp-change` acknowledgement, and the installer
  records the previous RDP / child-session values so `uninstall` can restore them.

**Out of scope**

- Malicious software already running as your user.
- Data exfiltration by the agent itself (it has your files by design).
- Antivirus / EDR false positives caused by automation, or by the unsigned installer.
- Games or services whose terms of use forbid automation.
- Windows editions that cannot host a child session (Home).

**Known limitations**

- The installer and the binaries are currently **unsigned**; SmartScreen will warn.
- AgentOrb changes Windows settings (Remote Desktop host, child sessions, per-user autostart)
  during installation. Review `AgentOrb.Setup.exe check` before and after.
- The NamedPipe status channel is optional for correctness: a broken UI never blocks tool calls.

## Safe harbour

We will not pursue legal action against researchers who report issues in good faith, follow
this policy, avoid privacy violations and service disruption, and give us reasonable time to
fix the issue before public disclosure.
