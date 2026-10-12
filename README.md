# SessionForge

A native Windows SSH and remote-session manager built with C# / .NET 8 / WPF.
Tabbed and split terminals, SFTP, an encrypted credential vault, jump hosts, port forwarding,
logging, macros, plus RDP, VNC, serial, Telnet and raw TCP.

> Status: first working build (0.1.0). See [docs/STATUS.md](docs/STATUS.md) for an honest,
> verified-versus-unverified feature matrix. Nothing is listed as complete there unless it was exercised.

## Build and run

Requirements: Windows 10 1809+ / Windows 11, .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`).

```powershell
dotnet build SessionForge.sln
dotnet run --project src/SessionForge.App
dotnet test                       # unit tests (SSH integration tests skip unless a server is configured)
pwsh tools/run-integration-tests.ps1   # starts a local OpenSSH sshd on :2222 and runs the SSH integration tests
pwsh tools/publish.ps1            # self-contained win-x64 build in artifacts/
```

Set `SESSIONFORGE_HOME` to use an isolated data folder (settings, sessions, vault, known hosts, logs).

## Layout

| Project | Purpose |
|---|---|
| `src/SessionForge.Terminal` | VT/xterm emulator (parser, screen buffer, scrollback, input encoding), no UI dependencies |
| `src/SessionForge.Core` | Models, JSON stores, credential vault, known hosts, importers (PuTTY, ssh_config) |
| `src/SessionForge.Protocols` | SSH (SSH.NET), SFTP, jump hosts, forwarding, agent, Telnet, serial, ConPTY, RFB/VNC, macros, logging |
| `src/SessionForge.App` | WPF shell, terminal renderer, dialogs, RDP host (mstscax ActiveX) |
| `tests/SessionForge.Tests` | xUnit tests |

Further reading: [ARCHITECTURE](docs/ARCHITECTURE.md), [SECURITY](docs/SECURITY.md),
[TESTING](docs/TESTING.md), [REA findings](docs/REA-Findings-MobaXterm.md).
