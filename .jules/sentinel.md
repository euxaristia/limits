## 2026-03-30 - Restrictive permissions on configuration files storing API keys
**Vulnerability:** `save_to` created `config.json` using default umask (`std::fs::write`), making plaintext API keys and cookies world-readable (`0o644`) to all local users.
**Learning:** Overwriting or creating files with default permissions does not restrict access on existing files or default umasks.
**Prevention:** Use `OpenOptionsExt::mode(0o600)` and `file.set_permissions` when saving credential-bearing config files on Unix systems.

## 2026-03-31 - Unbounded backward search in email redaction causing slice panic DoS
**Vulnerability:** `redact_emails` panicked with out-of-bounds slice range (`start > end`) when parsing adjacent/concatenated email strings because `local_part` searched backward past the `copied` string buffer offset.
**Learning:** Parsing routines scanning backwards from delimiters (`@`) must be bounded by the current write cursor (`copied`) to avoid overlap and slice index panics.
**Prevention:** Always pass the buffer progress offset (`min_start`) to backward scanning helper functions.

## 2026-04-01 - Parent directory of credential-bearing configuration files left with default umask permissions
**Vulnerability:** `save_to` set owner-only permissions (`0o600`) on `config.json`, but `std::fs::create_dir_all` created missing parent directories using default umask (`0o755`), leaving the directory world-readable and searchable by local users.
**Learning:** Restricting file permissions alone is insufficient if the parent directory permissions allow local users to traverse or inspect metadata.
**Prevention:** Use `DirBuilderExt::mode(0o700)` and `set_permissions` when creating credential-bearing configuration directories on Unix systems.
