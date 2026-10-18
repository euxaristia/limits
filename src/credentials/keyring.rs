//! Reading a secret out of the OS credential store.
//!
//! Only one provider needs this. Antigravity 1.1.x moved its live OAuth token
//! into the keyring and left the on-disk token file behind as a stale artifact,
//! so a reader that only looks at the file reports an expired token forever.
//!
//! The value is written by go-keyring, which base64-encodes payloads behind a
//! `go-keyring-base64:` marker and, on Windows, stores the entry as a generic
//! credential named `service:account`.
//!
//! An account of `None` matches on the service alone. Claude Code's macOS
//! entry needs that: it is keyed by the OS username, which this crate has no
//! business guessing.

use crate::credentials::headless;
use std::process::Command;

const GO_KEYRING_BASE64: &str = "go-keyring-base64:";

/// Fetch a secret, decoding UTF-16LE and go-keyring's base64 wrapper when
/// present. `account` of `None` matches any account under the service.
pub fn read(service: &str, account: Option<&str>) -> Option<Vec<u8>> {
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
fn read_raw(service: &str, account: Option<&str>) -> Option<Vec<u8>> {
    let Some(account) = account else {
        // Nothing on Windows can enumerate a target by service alone, so the
        // service name is the only candidate.
        return read_target(service);
    };
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
fn read_raw(service: &str, account: Option<&str>) -> Option<Vec<u8>> {
    let mut command = Command::new("security");
    command.args(["find-generic-password", "-s", service]);
    if let Some(account) = account {
        command.args(["-a", account]);
    }
    run(command.arg("-w"))
}

#[cfg(all(unix, not(target_os = "macos")))]
fn read_raw(service: &str, account: Option<&str>) -> Option<Vec<u8>> {
    let mut command = Command::new("secret-tool");
    command.args(["lookup", "service", service]);
    if let Some(account) = account {
        command.args(["username", account]);
    }
    if let Some(secret) = run(&mut command).filter(|s| !s.is_empty()) {
        return Some(secret);
    }
    read_raw_python(service, account)
}

#[cfg(all(unix, not(target_os = "macos")))]
const SECRET_SERVICE_SCRIPT: &str = r#"
import sys

def get_secret(service, account):
    try:
        import dbus
        bus = dbus.SessionBus()
        ss = dbus.Interface(
            bus.get_object('org.freedesktop.secrets', '/org/freedesktop/secrets'),
            'org.freedesktop.Secret.Service',
        )
        session = ss.OpenSession('plain', '')[1]
        search = {'service': service}
        if account:
            search['username'] = account
        unlocked, locked = ss.SearchItems(search)
        items = unlocked + locked
        if not items and account:
            unlocked, locked = ss.SearchItems({'service': service, 'account': account})
            items = unlocked + locked
        if not items and account:
            unlocked, locked = ss.SearchItems({'service': service, 'user': account})
            items = unlocked + locked
        if items:
            item = bus.get_object('org.freedesktop.secrets', items[0])
            secret = item.GetSecret(session, dbus_interface='org.freedesktop.Secret.Item')
            return bytes(secret[2])
    except Exception:
        pass
    try:
        import secretstorage
        bus = secretstorage.dbus_init()
        col = secretstorage.get_default_collection(bus)
        search = {'service': service}
        if account:
            search['username'] = account
        items = list(col.search_items(search))
        if not items and account:
            items = list(col.search_items({'service': service, 'account': account}))
        if not items and account:
            items = list(col.search_items({'service': service, 'user': account}))
        if items:
            return items[0].get_secret()
    except Exception:
        pass
    return None

sec = get_secret(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 and sys.argv[2] != '' else None)
if sec:
    sys.stdout.buffer.write(sec)
    sys.exit(0)
sys.exit(1)
"#;

/// Build the fallback interpreter invocation. `-I` (isolated mode) must come
/// before `-c`: without it, Python seeds `sys.path` from the working
/// directory and honours `PYTHONPATH` and the user site-packages, so a
/// `dbus.py` or `secretstorage.py` planted wherever the user happens to run
/// `limits` would shadow the real modules inside a process that is about to
/// read keyring secrets. Isolated mode drops all of those entries, leaving
/// only the standard library and site-packages. `run` still routes the child
/// through `headless::prepare`, which moves it into the temp directory, but
/// the shared temp directory is itself plantable, so it is not a substitute.
#[cfg(all(unix, not(target_os = "macos")))]
fn python_fallback_command(service: &str, account: Option<&str>) -> Command {
    let mut command = Command::new("python3");
    command.args([
        "-I",
        "-c",
        SECRET_SERVICE_SCRIPT,
        service,
        account.unwrap_or(""),
    ]);
    command
}

#[cfg(all(unix, not(target_os = "macos")))]
fn read_raw_python(service: &str, account: Option<&str>) -> Option<Vec<u8>> {
    let mut command = python_fallback_command(service, account);
    run(&mut command).filter(|s| !s.is_empty())
}

#[cfg(not(any(unix, target_os = "windows")))]
fn read_raw(_service: &str, _account: Option<&str>) -> Option<Vec<u8>> {
    None
}

#[cfg_attr(target_os = "windows", expect(dead_code))]
fn run(command: &mut Command) -> Option<Vec<u8>> {
    headless::prepare(command);
    let output = command.output().ok()?;
    output.status.success().then_some(output.stdout)
}

#[cfg(all(test, unix, not(target_os = "macos")))]
mod tests {
    use super::*;

    fn fallback_args(service: &str, account: Option<&str>) -> Vec<String> {
        let command = python_fallback_command(service, account);
        assert_eq!(command.get_program(), "python3");
        command
            .get_args()
            .map(|arg| arg.to_string_lossy().into_owned())
            .collect()
    }

    /// The fallback interpreter handles keyring secrets, so it must run in
    /// isolated mode: `-I` first, then `-c`, so no module planted in the
    /// working directory (or on `PYTHONPATH`, or in the user site-packages)
    /// can shadow the `dbus` and `secretstorage` imports in the script.
    #[test]
    fn python_fallback_runs_the_interpreter_isolated() {
        let args = fallback_args("some-service", Some("some-account"));
        assert_eq!(
            args,
            vec![
                "-I",
                "-c",
                SECRET_SERVICE_SCRIPT,
                "some-service",
                "some-account",
            ]
        );
    }

    /// A missing account still passes the empty positional argument the
    /// script keys its `sys.argv` checks on.
    #[test]
    fn python_fallback_passes_an_empty_account_argument() {
        let args = fallback_args("some-service", None);
        assert_eq!(args.len(), 5);
        assert_eq!(args[4], "");
    }
}
