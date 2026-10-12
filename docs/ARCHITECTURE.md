# Architecture

## Layers

```
App (WPF)  ──►  Protocols  ──►  Core  ──►  (BCL)
   │               │
   └──────►  Terminal (no UI deps)
```

- **Terminal**: `VtParser` feeds `TerminalEmulator` (CSI/OSC/text handlers split into partial files). The
  screen is a `LineRing` of `TerminalLine`s of `Cell`s. `InputEncoder` converts key events to byte sequences.
- **Core**: pure models and persistence. `DocumentStore`/`JsonFile` do atomic JSON writes. `CredentialVault`
  holds a random data key wrapped by an Argon2id-derived key (optionally also by DPAPI).
  `KnownHostsStore` implements trust-on-first-use.
- **Protocols**: everything that talks to the network or OS. Terminal-like sessions implement
  `ITerminalTransport` (SSH, Telnet/TCP, serial, ConPTY). `SshConnector` builds connections, resolving
  jump-host chains through ephemeral local forwards (`GatewayTunnel`). User interaction (host-key prompts,
  passwords) goes through `IConnectionInteraction`, so protocol code has no UI references.
- **App**: `MainWindow` hosts workspace tabs. Each tab wraps a `SessionHost` subclass
  (`TerminalSessionHost`, `SftpSessionHost`, `RdpSessionHost`, `VncSessionHost`). `TerminalRenderer` draws one
  `DrawingVisual` per row and redraws only dirty rows.

## RDP host

The mstscax ActiveX control is wrapped in a WinForms `AxHost` inside a `WindowsFormsHost`. It is created in the
host's constructor and attached before the view loads, so its window is never built during a layout pass.
`ConnectAsync` waits for the view's `Loaded`, then for the control handle, and applies settings through
late-bound COM. Registered classes are probed before use because AxHost instantiates lazily.

## Threading

UI on the dispatcher. Transports read on background tasks and post data to the terminal on the dispatcher.
Unhandled UI exceptions are logged and surfaced through a single deferred, de-duplicated dialog.
