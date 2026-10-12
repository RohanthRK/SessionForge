# REA investigation: MobaXterm 26.5 (reference application)

Purpose: establish a **verified feature inventory** of the reference product so SessionForge's
scope is grounded in evidence rather than memory. No MobaXterm code, assets, icons, fonts, text
or protocols were copied into SessionForge; findings were used only as a feature checklist.

## Subject

| Field | Value |
|---|---|
| File | `MobaXterm Main Files/MobaXterm/MobaXterm.exe` |
| Version (`version.dat`) | 26.5 build 5542 |
| SHA-256 | `f0e77836182fe52e66c68f88fbe13801f935f100623103df1d8be2181294e8e5` |
| Size | 16,601,672 bytes |
| Format | PE32, machine 0x14C (i386) — native, not .NET |
| Companion files | `CygUtils.plugin`, `CygUtils64.plugin`, `MobaRTE.exe`, `MobaXterm_installer.dat` |

## Method and tool limits

REA 6.4.0 CLI (`node rea/scripts/rea.mjs`), run locally, nothing executed:

1. `inspect-artifact` → single PE node, no child containers (observed).
2. `inspect-binary-layout` → **unavailable** (provider supports Linux x64 ELF only).
3. `inspect-pe-resources` → 221 resource entries with offsets and SHA-256 (observed).
4. Resource payloads were sliced from the file at REA-reported offsets and the `RT_RCDATA`
   `TFORM*`/`TFRAME*` entries decoded with a small binary-DFM (`TPF0`) reader — this is how
   Delphi stores form layouts, which lets us read every dialog's controls and labels.

No deep disassembly was possible: `rea doctor` reports no Hopper/Ghidra/IDA provider. So
**internal algorithms are unknown**; everything below is UI/resource-level evidence.

Confidence legend: **Observed** = read directly from shipped bytes. **Inferred** = reasonable
reading of observed labels; not behaviourally verified.

## Architecture evidence

- Delphi/VCL application (TPF0 forms, `DVCLAL`, SynEdit resources) — observed.
- Embedded changelog states the terminal is **PuTTY-based**, and that it embeds **OpenSSL**,
  an **OpenSSH** command, and an **Xorg** server — observed (changelog text, author-stated).
- Unix tooling ships in the `CygUtils*.plugin` archives (Cygwin) — observed by name.
- Custom resources (`MOBASE`, `TXTRES`, `HOTKEYS`, `SEPARAMS`, `BASEPROF`, `LI`, `XW`) are
  compressed/obfuscated; not decoded (deliberately — not needed for a feature inventory).

## Feature inventory (from 64 decoded forms)

### Session types (`TFORMSESSIONEDIT`) — Observed
SSH, Telnet, Rsh, XDMCP, RDP, VNC, FTP, SFTP, Serial, Shell (local), File, Mosh, Browser,
AWS S3 (experimental), WSL.

### SSH session settings — Observed
- Remote host, username, port; "Use private key"; execute command after connect
  ("Do not exit after command ends"); X11 forwarding; compression; remote desktop environment.
- SSH-browser type: None / SFTP / SCP (enhanced) / SCP (normal); "follow SSH path".
- Proxy: None, SOCKS4, SOCKS5, HTTP, Telnet, Local command, SSH forwarding, SSH command.
- "SSH gateway (jump host)" → `TFORMJUMPHOSTS`: ordered **list** of jump hosts (add, move
  up/down, remove).
- Bookmark: session name, icon, comments, tab color, desktop shortcut, start as
  normal/detached/maximized/fullscreen tab, macro at session start, lock terminal title,
  reconnect message at session end.

### Expert SSH (`TFORMSSHPROTOSETTINGS`) — Observed
Ordered preference lists with a "=Warn below=" divider for: KEX (ECDH, NTRU-Curve25519,
ML-KEM-Curve25519, ML-KEM-NIST-ECDH, DH group-exchange/14/15/16/17/18, RSA, group1), host
key types (Ed448, Ed25519, ECDSA, RSA, DSA) and ciphers (AES, ChaCha20, AES-GCM, 3DES, DES,
Blowfish, Arcfour). Agent forwarding, agent authentication, "disconnect if authentication
succeeds trivially", prefer already-known host key algorithms, host certificates, user
certificates, smart card certificates, locale forwarding, KexInit workaround.

### Host key verification (`TFORMHKFINGERPRINTDIALOG`) — Observed
Shows SHA-256 and MD5 fingerprints and full public key; user can paste the expected key to compare.
Global setting "Validate host identity at first connection".

### Port forwarding (`TFORMPORTFORWARDINGEDIT`) — Observed
Local, remote and dynamic (SOCKS) forwarding, with a diagram. A separate "MobaSSHTunnel"
tool manages saved tunnels.

### Credential vault (`TFORMMASTERPASSWORD`, `TFORMSTOREPASSWORDS`, `TFORMMANAGEPASSWORDS`, `TFORMPROMPTMP`) — Observed
- Master password protects stored passwords. UI text states **AES-256-CBC-SHA512** with a key
  derived from the master password (author-stated, unverified).
- Strength meter; prompt policy: only on new Windows account/computer, at every startup, or
  at startup and after session unlock.
- Save passwords Always/Never/Ask; optionally save key passphrases; storage location
  registry vs config file; named **Credentials** entries (copy username/password).
- Re-authentication with master password, Windows password or **Windows Hello** before
  revealing secrets.

### Terminal (`TFORMSESSIONEDIT`, `TFORMADVANCEDTERMSETTINGS`, `TFORMXTERM`) — Observed
Terminal types (xterm, xterm-256color, vt100/220/…, linux…), charsets (ISO-8859-x, UTF-8, KOI8, GBK,
Big5, SJIS, CP4xx…), cursor shapes (block/underline/line, blinking variants), backspace ^H,
bold-as-colour, keep rows/cols on resize, syntax highlighting (keywords, shell, Cisco, Perl, SQL),
paste delay, auto-copy on select, word delimiters, scrollbar, implicit CR/LF, xterm mouse
reporting, alternate screen, scrollback lines, bell type, answerback, reconnect/save/exit keys,
**log terminal output to file**, find in terminal (case, regex, reverse), paste safety warnings
(multi-line, homograph).

### Workspace UI (`TFORMXTERM`, `TFORMDETACHEDTAB`, `TFRAMEHOME`) — Observed
Tabs (rename, colour, duplicate, close left/right/others/all, pin, detach/re-attach, always on
top, fullscreen on 2nd/all monitors), **split modes: single, 2 horizontal, 2 vertical, 4
terminals**, **MultiExec** (type into all terminals), sessions sidebar with filter, quick
connect, home tab with recent sessions, light/dark theme, compact/fullscreen modes, toolbar
customisation, zoom in/out, save/print terminal output.

### SFTP browser (`TFRAMEFTP`) — Observed
Open / open with editor / default program, compare, download, upload, delete, rename, copy path,
properties, permissions (`TFORMCHMOD`), new directory/file, refresh, transfer progress & speed.

### Automation (`TFORMMANAGEMACROS`, `TFORMEDITMACROLINE`) — Observed
Macros with hotkeys; steps: text, key press, sleep, **wait for pattern**, call another macro,
mouse/keyboard events; keystroke delay; record macro (terminal, graphical, desktop).

### Remote desktop — Observed
- RDP: resolution presets/fit, zoom, smart sizing, autoscale, compression, redirect
  drives/printers/ports/smartcards/clipboard/microphone/audio/WebAuthn, admin console, server
  authentication level, forward keyboard shortcuts.
- VNC: fullscreen, autoscale, view-only, respect ratio, shared clipboard, record, Ctrl+Alt+Del.
  Changelog: Tight, Hextile, RRE, TRLE encodings; Tight/SASL/UltraVNC/MsLogon2 auth.

### Serial (`TFORMCHOOSECOMPORT`, session edit) — Observed
COM port picker; speeds 1200–921600; (flow control/parity labels inferred from session form).

### Other — Observed
Shared sessions (shared folder/backend, refresh interval, edit permissions), import/export
configuration, session presets, CSV import, network scanner, ports scanner, netstat, WOL,
key generator (MobaKeyGen), SSH agents (internal MobAgent, PuTTY agent, Windows agent),
GSSAPI/Kerberos, 2FA for gateways, keepalive, security "Customizer" presets (disable password
saving, disable features).

## Out of scope for SessionForge (deliberate)
Embedded X server/XDMCP, Cygwin Unix tools, Mosh, AWS S3, terminal games, MobaApt, plugin
creator — they are separate products bundled with MobaXterm and not core to an SSH/remote
session manager. WSL and Windows shells are covered via ConPTY local terminals instead.

## Reproduction
```
node rea/scripts/rea.mjs inspect-artifact "<path>/MobaXterm.exe" --format json
node rea/scripts/rea.mjs inspect-pe-resources "<path>/MobaXterm.exe" --format json
```
Then slice each `RT_RCDATA` (`type id 10`) payload at `payload.location` and decode `TPF0`.
