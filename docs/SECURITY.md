# Security notes

- **Vault**: random 256-bit data key; entries encrypted with AES-256-GCM. The key is wrapped with a key derived
  from the master password via Argon2id, and optionally also protected with Windows DPAPI (current user) for
  automatic unlock. The master password is never stored.
- **Secrets at rest**: session files hold references to vault entries, not passwords, unless the user chooses
  "save password", which stores the secret in the vault.
- **Host keys**: trust on first use; a changed key blocks the connection with an explicit warning.
- **Keys**: private keys are read, used in memory and not copied. Generated keys use OpenSSH format with
  bcrypt_pbkdf and AES-CTR when a passphrase is set.
- **Local ports**: forwards and jump-host tunnels bind to 127.0.0.1 only.
- **Logging**: diagnostics log excludes secrets; session logs are the user's choice and can contain anything shown
  in the terminal.
- **RDP**: the control receives the password in memory (`ClearTextPassword`); credentials are not written to
  .rdp files.

Limitations: no external security review has been done; in-memory secrets are not pinned or zeroed
comprehensively (managed strings). Treat this as a first build, not an audited product.
