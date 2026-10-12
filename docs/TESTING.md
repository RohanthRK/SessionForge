# Testing

- `dotnet test` runs xUnit tests: terminal emulator and input encoding, vault, stores and importers, key
  handling, protocol framing. Integration tests use `SkippableFact` and skip without a server.
- `tools/run-integration-tests.ps1` starts the Win32-OpenSSH `sshd` on port 2222 with a throwaway config and
  host key, then runs `SshIntegrationTests` (TOFU, changed host key, interactive shell, jump host, SFTP, local/dynamic forwarding, encrypted key, wrong key).
- GUI checks were done by launching the app with `SESSIONFORGE_HOME` pointing at a scratch folder and capturing
  screenshots (home, local terminal, session editor, host-key dialog, SFTP, split panes, multi-exec, RDP error path).

Also covered by loopback tests: raw TCP and Telnet transports, and the RFB client against an in-process server.

Not covered: VNC against a real server (compressed encodings), serial hardware, Pageant auth, proxies,
a full RDP desktop session.
