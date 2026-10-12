# Status

Legend: **Verified** = exercised in this environment (automated test or driven in the running app).
**Implemented** = code complete and builds, but not exercised against a real counterpart.
**Missing** = not implemented.

Test results at last run: 110 unit tests pass, 8 SSH integration tests pass against a real
Win32-OpenSSH `sshd` on 127.0.0.1:2222 (they skip when no server is configured).

| Area | Status | Notes |
|---|---|---|
| Terminal emulator (VT100/xterm, 256/truecolor, alt screen, scrollback, mouse, bracketed paste) | Verified | Unit tests plus local shell in the GUI |
| Tabs, split panes, detach, multi-exec | Verified | Driven in the GUI |
| Local shell (ConPTY) | Verified | |
| SSH: password, keyboard-interactive, public key | Verified | Integration tests against real sshd |
| SSH: TOFU host-key verification and change warning | Verified | Integration test and GUI dialog |
| SSH: jump-host chain | Verified | Integration test (local forward chain) |
| SSH: port forwarding (local, dynamic/SOCKS) | Verified | Integration test |
| SSH: remote port forwarding | Implemented | Not covered by a test |
| SSH: agent auth (OpenSSH pipe, Pageant) | Implemented | Not tested against a running agent |
| SSH: proxy (HTTP/SOCKS) | Implemented | Not tested against a proxy |
| SFTP browser, transfers, chmod | Verified | Integration tests and GUI panel |
| Credential vault (AES-256-GCM, Argon2id, optional DPAPI) | Verified | Unit tests |
| OpenSSH key generation / import (incl. encrypted keys) | Verified | Unit tests |
| Session logging | Verified | Unit tests |
| Macros / automation | Verified | Unit tests; runner lightly tested live |
| Telnet, raw TCP | Implemented | Protocol unit tests; no live server |
| Serial | Implemented | No hardware available |
| VNC (RFB client) | Implemented | Protocol code only; not run against a VNC server |
| RDP (mstscax ActiveX) | Partially verified | Control creates and connects; the real mstsc client reported a genuine server-side error on loopback. No RDP host is available here, so a full desktop session was never seen |
| Import: PuTTY, ssh_config | Verified | Unit tests |
| X11 forwarding, SSH agent forwarding | Missing | |
| Shared/collaborative sessions, printing | Missing | |
| Installer / signed binaries | Missing | `tools/publish.ps1` produces a self-contained folder only |

## Known gaps

- DPI: developed on a 200% display; other scales have not been inspected visually.
- RDP fallback: ProgID `MsTscAx.MsTscAx.13` is registered but not instantiable on the dev machine;
  the host probes each class and falls back to a working one.
- Accessibility (screen reader support in the terminal surface) has not been evaluated.
