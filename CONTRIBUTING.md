# Contributing to AgentOrb

Thanks for taking the time to contribute. This project is small, Windows-specific and
deliberately dependency-free — please keep it that way where you can.

## Development environment

- Windows 10/11 Pro or Enterprise, x64
- .NET 8 SDK
- Visual Studio 2022, Rider, or VS Code with the C# extension

```bat
git clone <your-fork>
cd AgentOrb
dotnet build AgentOrb.sln -c Release
dotnet run --project tests\AgentOrb.Tests\AgentOrb.Tests.csproj -c Release
```

The test project is a plain console runner (no xUnit/NUnit) so the repository keeps zero
NuGet dependencies. `dotnet run` prints `PASS`/`FAIL` per test and exits non-zero on failure.

Running the desktop app during development:

```bat
dotnet run --project src\AgentOrb.Desktop -c Debug -- --demo
```

`--demo` cycles status events through the island without starting Anode or a seat.

## Ground rules

These are not style preferences; breaking them has caused real bugs:

1. **Single instance is machine-wide.** The mutex must stay `Global\...`. The Agent seat logs
   in as the same user, so a per-user `Local\` mutex lets two instances run and the two pipe
   servers split status events unpredictably.
2. **Never set `PipeOptions.CurrentUserOnly` on the pipe client.** .NET verifies the server
   with `OpenProcess`, which fails across sessions and packaged-app containers. The server's
   kernel ACL already restricts the pipe to the current user.
3. **The island window never changes size or origin.** Animate the island inside the fixed
   460×120 window; the window must keep `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`.
4. **Never log or publish MCP arguments, results or secrets.** Status messages are fixed,
   human-readable Chinese strings by design.
5. **No new NuGet packages without discussion.** Open an issue first; the current dependency
   surface is zero and that is a feature.
6. **Keep Windows system changes reversible.** Anything the installer changes (RDP, child
   sessions, autostart, Codex config) must be restored by `uninstall`.

## Making a change

1. Open an issue describing the problem or proposal (search first).
2. Fork, branch from `main` (`fix/...`, `feat/...`, `docs/...`).
3. Keep commits focused; write messages in the imperative mood.
4. Add or update tests for behaviour changes. UI-only changes need a screenshot or a short
   screen recording in the pull request.
5. Run `dotnet build -c Release` and the test runner before pushing — both must be clean.
6. Open a pull request describing the change, the motivation, and how you verified it on a
   real Windows machine (VM-only verification is not enough for input isolation changes).

## What we cannot accept

- Changes that weaken the input isolation between the user's session and the seat.
- Telemetry, analytics or network calls that are on by default.
- Bundling third-party binaries whose licences are incompatible with MIT.
- Bypassing the installer's explicit RDP consent flow.

## Reporting bugs

Include the AgentOrb version (`AgentOrb.Setup.exe check` output), Windows edition and build,
the MCP client you used, and the relevant lines from `%LOCALAPPDATA%\Anode\anode.log`.
Redact paths that contain your user name if you prefer.

Security issues: please follow [SECURITY.md](SECURITY.md) instead of opening a public issue.

## Licence

By contributing you agree that your contribution is licensed under the MIT licence of this
repository.
