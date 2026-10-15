## 2026-03-30 - Restrictive permissions on configuration files storing API keys
**Vulnerability:** `save_to` created `config.json` using default umask (`std::fs::write`), making plaintext API keys and cookies world-readable (`0o644`) to all local users.
**Learning:** Overwriting or creating files with default permissions does not restrict access on existing files or default umasks.
**Prevention:** Use `OpenOptionsExt::mode(0o600)` and `file.set_permissions` when saving credential-bearing config files on Unix systems.
