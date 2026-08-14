//! Reading a secret out of the OS credential store.
//!
//! Only one provider needs this. Antigravity 1.1.x moved its live OAuth token
//! into the keyring and left the on-disk token file behind as a stale artifact,
//! so a reader that only looks at the file reports an expired token forever.
//!
//! The value is written by go-keyring, which base64-encodes payloads behind a
//! `go-keyring-base64:` marker and, on Windows, stores the entry as a generic
//! credential named `service:account`.

use crate::credentials::headless;
use std::process::Command;

const GO_KEYRING_BASE64: &str = "go-keyring-base64:";

/// Fetch a secret, decoding UTF-16LE and go-keyring's base64 wrapper when present.
pub fn read(service: &str, account: &str) -> Option<Vec<u8>> {
    let raw = read_raw(service, account)?;
    let text = decode_secret_bytes(&raw);
    let trimmed = text.trim();
    if trimmed.is_empty() {
        return None;
    }
    match trimmed.strip_prefix(GO_KEYRING_BASE64) {
        Some(encoded) => super::base64_decode(encoded),
        None => Some(trimmed.as_bytes().to_vec()),
    }
}

fn decode_secret_bytes(raw: &[u8]) -> String {
    // Windows keyring crate (windows-native-keyring-store) stores password strings as UTF-16LE.
    if raw.len() >= 2 && raw.len().is_multiple_of(2) && raw[1] == 0 && raw[0] != 0 {
        let (chunks, _) = raw.as_chunks::<2>();
        let units: Vec<u16> = chunks
            .iter()
            .map(|chunk| u16::from_le_bytes(*chunk))
            .collect();
        if let Ok(text) = String::from_utf16(&units) {
            return text;
        }
    }
    String::from_utf8_lossy(raw).to_string()
}

#[cfg(target_os = "windows")]
fn read_raw(service: &str, account: &str) -> Option<Vec<u8>> {
    for target_name in [
        format!("{service}:{account}"),
        format!("{account}.{service}"),
        format!("{service}/{account}"),
        format!("{service}.{account}"),
    ] {
        if let Some(secret) = read_target(&target_name) {
            return Some(secret);
        }
    }
    None
}

#[cfg(target_os = "windows")]
fn read_target(target_name: &str) -> Option<Vec<u8>> {
    use std::os::windows::ffi::OsStrExt;
    use windows_sys::Win32::Security::Credentials::{
        CRED_TYPE_GENERIC, CREDENTIALW, CredFree, CredReadW,
    };

    let target: Vec<u16> = std::ffi::OsStr::new(target_name)
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();

    // SAFETY: `target` is a NUL-terminated UTF-16 string that outlives the
    // call. On success CredReadW writes an owned pointer into `credential`,
    // which is read only before CredFree and never after.
    unsafe {
        let mut credential: *mut CREDENTIALW = std::ptr::null_mut();
        if CredReadW(target.as_ptr(), CRED_TYPE_GENERIC, 0, &mut credential) == 0
            || credential.is_null()
        {
            return None;
        }
        let blob = (*credential).CredentialBlob;
        let size = (*credential).CredentialBlobSize as usize;
        let secret =
            (!blob.is_null() && size > 0).then(|| std::slice::from_raw_parts(blob, size).to_vec());
        CredFree(credential.cast());
        secret
    }
}

#[cfg(target_os = "macos")]
fn read_raw(service: &str, account: &str) -> Option<Vec<u8>> {
    run(Command::new("security").args([
        "find-generic-password",
        "-s",
        service,
        "-a",
        account,
        "-w",
    ]))
}

#[cfg(all(unix, not(target_os = "macos")))]
fn read_raw(service: &str, account: &str) -> Option<Vec<u8>> {
    run(Command::new("secret-tool").args(["lookup", "service", service, "username", account]))
}

#[cfg(not(any(unix, target_os = "windows")))]
fn read_raw(_service: &str, _account: &str) -> Option<Vec<u8>> {
    None
}

#[cfg_attr(target_os = "windows", expect(dead_code))]
fn run(command: &mut Command) -> Option<Vec<u8>> {
    headless::prepare(command);
    let output = command.output().ok()?;
    output.status.success().then_some(output.stdout)
}
