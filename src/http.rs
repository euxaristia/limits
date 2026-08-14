//! The network seam.
//!
//! Every provider reads its quota through [`HttpClient`], never through a
//! concrete client. That exists for one concrete reason: the host application
//! this crate was built for (cairn-code) already owns a hardened HTTP path and
//! has no async runtime, so a transitive `tokio`/`reqwest` tree would be a
//! large, duplicated dependency it does not want. Implementing this one trait
//! lets it route quota reads through the client it already audits.
//!
//! Standing alone, [`CurlClient`] is the default and needs nothing but `curl`
//! on `PATH` (present by default on Windows 10 1803+, macOS, and effectively
//! every Linux install).
//!
//! A completed response is always `Ok`, whatever its status. Fetchers must be
//! able to tell a 401 (refresh the token and retry) from a 404 (stop), and
//! folding both into an error string loses that distinction.

use std::fmt;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Method {
    Get,
    Post,
}

impl Method {
    fn as_str(self) -> &'static str {
        match self {
            Method::Get => "GET",
            Method::Post => "POST",
        }
    }
}

#[derive(Clone, Debug)]
pub struct HttpRequest {
    pub method: Method,
    pub url: String,
    pub headers: Vec<(String, String)>,
    pub body: Option<String>,
}

impl HttpRequest {
    pub fn get(url: impl Into<String>) -> Self {
        HttpRequest {
            method: Method::Get,
            url: url.into(),
            headers: Vec::new(),
            body: None,
        }
    }

    pub fn post(url: impl Into<String>, body: impl Into<String>) -> Self {
        HttpRequest {
            method: Method::Post,
            url: url.into(),
            headers: Vec::new(),
            body: Some(body.into()),
        }
    }

    pub fn header(mut self, name: impl Into<String>, value: impl Into<String>) -> Self {
        self.headers.push((name.into(), value.into()));
        self
    }

    /// Add a header only when the value is non-empty, which is how optional
    /// account identifiers are attached without an `if` at every call site.
    pub fn optional_header(self, name: &str, value: &str) -> Self {
        if value.trim().is_empty() {
            self
        } else {
            self.header(name, value.trim())
        }
    }

    pub fn bearer(self, token: &str) -> Self {
        self.header("Authorization", format!("Bearer {}", token.trim()))
    }
}

#[derive(Clone, Debug)]
pub struct HttpResponse {
    pub status: u16,
    pub body: String,
}

impl HttpResponse {
    pub fn is_success(&self) -> bool {
        (200..300).contains(&self.status)
    }

    /// True for the statuses that mean "this token is no longer good", which is
    /// the signal to run a CLI probe and try once more.
    pub fn is_auth_failure(&self) -> bool {
        matches!(self.status, 401 | 403)
    }

    pub fn json(&self) -> Result<serde_json::Value, serde_json::Error> {
        serde_json::from_str(&self.body)
    }
}

/// A request that never produced a response.
#[derive(Clone, Debug)]
pub struct HttpError(pub String);

impl fmt::Display for HttpError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for HttpError {}

impl From<String> for HttpError {
    fn from(value: String) -> Self {
        HttpError(value)
    }
}

impl From<&str> for HttpError {
    fn from(value: &str) -> Self {
        HttpError(value.to_string())
    }
}

/// Something that can perform a request. Implement this to route quota reads
/// through a host application's own HTTP stack.
pub trait HttpClient: Send + Sync {
    fn send(&self, request: &HttpRequest) -> Result<HttpResponse, HttpError>;
}

impl<T: HttpClient + ?Sized> HttpClient for std::sync::Arc<T> {
    fn send(&self, request: &HttpRequest) -> Result<HttpResponse, HttpError> {
        (**self).send(request)
    }
}

/// Quota endpoints answer in well under a second or they are not answering.
const CONNECT_TIMEOUT: Duration = Duration::from_secs(8);
const TOTAL_TIMEOUT: Duration = Duration::from_secs(15);
/// A quota payload is a few kilobytes. This bounds what a hostile or broken
/// endpoint can make the process buffer.
const RESPONSE_CAP_BYTES: usize = 2 * 1024 * 1024;
const STDERR_CAP_BYTES: usize = 16 * 1024;

/// The default transport: one `curl` subprocess per request.
#[derive(Clone, Debug)]
pub struct CurlClient {
    connect_timeout: Duration,
    total_timeout: Duration,
}

impl Default for CurlClient {
    fn default() -> Self {
        CurlClient {
            connect_timeout: CONNECT_TIMEOUT,
            total_timeout: TOTAL_TIMEOUT,
        }
    }
}

impl CurlClient {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn with_timeouts(connect: Duration, total: Duration) -> Self {
        CurlClient {
            connect_timeout: connect,
            total_timeout: total,
        }
    }
}

impl HttpClient for CurlClient {
    fn send(&self, request: &HttpRequest) -> Result<HttpResponse, HttpError> {
        check_control_chars("URL", &request.url)?;

        // Headers carry bearer tokens and session cookies. Passing them as
        // arguments would publish them to every process that can read the
        // process list, so they go through an owner-only file instead.
        let config = CurlConfig::write(&request.headers)?;

        let mut command = Command::new("curl");
        // Must be first: without it curl reads ~/.curlrc, which can redirect
        // the request through a proxy or rewrite the URL.
        command.arg("-q");
        command.args([
            "-sS",
            "-i",
            "-X",
            request.method.as_str(),
            // A malformed URL must not be able to reach file:, scp:, or dict:.
            "--proto",
            "=http,https",
            "--connect-timeout",
            &format!("{}", self.connect_timeout.as_secs()),
            "--max-time",
            &format!("{}", self.total_timeout.as_secs()),
        ]);
        command.arg("--config").arg(config.path());

        let body = request.body.clone();
        if body.is_some() {
            // Streamed rather than passed as an argument, for the same reason
            // as the headers.
            command.args(["--data-binary", "@-"]);
            command.stdin(Stdio::piped());
        } else {
            command.stdin(Stdio::null());
        }
        // Everything after `--` is a URL, so it goes last.
        command.arg("--").arg(&request.url);
        command.stdout(Stdio::piped());
        command.stderr(Stdio::piped());

        let mut child = command
            .spawn()
            .map_err(|e| HttpError(format!("could not run curl: {e}")))?;

        if let Some(body) = body
            && let Some(mut stdin) = child.stdin.take()
        {
            std::thread::spawn(move || {
                let _ = stdin.write_all(body.as_bytes());
            });
        }

        let mut stdout = child.stdout.take().expect("stdout is piped");
        let mut stderr = child.stderr.take().expect("stderr is piped");

        // Both pipes are drained concurrently: reading one to the end while the
        // other fills its buffer would deadlock.
        let (raw, errors) = std::thread::scope(|scope| {
            let errors = scope.spawn(move || read_capped(&mut stderr, STDERR_CAP_BYTES));
            let raw = read_capped(&mut stdout, RESPONSE_CAP_BYTES);
            (raw, errors.join().unwrap_or_default())
        });

        let status = child
            .wait()
            .map_err(|e| HttpError(format!("failed waiting for curl: {e}")))?;
        drop(config);

        if !status.success() {
            let detail = String::from_utf8_lossy(&errors).trim().to_string();
            return Err(HttpError(if detail.is_empty() {
                format!("curl exited with {status}")
            } else {
                detail
            }));
        }

        parse_response(&String::from_utf8_lossy(&raw))
    }
}

/// Split `curl -i` output into a status code and a body.
///
/// A redirect or a `100 Continue` produces more than one header block, so the
/// last one is the response that matters.
fn parse_response(raw: &str) -> Result<HttpResponse, HttpError> {
    let mut rest = raw;
    let mut status = None;

    loop {
        let split = rest
            .find("\r\n\r\n")
            .map(|i| i + 4)
            .or_else(|| rest.find("\n\n").map(|i| i + 2));
        let Some(split) = split else { break };

        let head = &rest[..split];
        let Some(line) = head.lines().next() else {
            break;
        };
        if !line.starts_with("HTTP/") {
            break;
        }
        status = line
            .split_whitespace()
            .nth(1)
            .and_then(|code| code.parse::<u16>().ok());
        rest = &rest[split..];

        // Only an interim or redirect block is followed by another one.
        if !rest.starts_with("HTTP/") {
            break;
        }
    }

    match status {
        Some(status) => Ok(HttpResponse {
            status,
            body: rest.to_string(),
        }),
        None => Err(HttpError("curl returned no HTTP status line".into())),
    }
}

fn read_capped<R: Read>(reader: &mut R, cap: usize) -> Vec<u8> {
    let mut buffer = Vec::new();
    // One byte past the cap, so a truncated read is detectable if that ever
    // needs reporting; the excess is discarded either way.
    let _ = reader.take(cap as u64 + 1).read_to_end(&mut buffer);
    buffer.truncate(cap);
    buffer
}

fn check_control_chars(what: &str, value: &str) -> Result<(), HttpError> {
    if value.chars().any(char::is_control) {
        return Err(HttpError(format!("{what} contains a control character")));
    }
    Ok(())
}

static CONFIG_SEQUENCE: AtomicU64 = AtomicU64::new(0);

/// A `curl --config` file holding the request headers, owner-readable only,
/// removed when dropped.
struct CurlConfig(PathBuf);

impl CurlConfig {
    fn write(headers: &[(String, String)]) -> Result<Self, HttpError> {
        let mut content = String::new();
        for (name, value) in headers {
            check_control_chars("header name", name)?;
            check_control_chars("header value", value)?;
            let escaped = value.replace('\\', "\\\\").replace('"', "\\\"");
            content.push_str(&format!("header = \"{name}: {escaped}\"\n"));
        }
        // Keeps the response to a single header block for POSTs with a body.
        content.push_str("header = \"Expect:\"\n");

        let dir = std::env::temp_dir();
        loop {
            let sequence = CONFIG_SEQUENCE.fetch_add(1, Ordering::Relaxed);
            let path = dir.join(format!(
                ".limits-curl-{}-{sequence}.conf",
                std::process::id()
            ));

            let mut options = std::fs::OpenOptions::new();
            options.write(true).create_new(true);
            #[cfg(unix)]
            {
                use std::os::unix::fs::OpenOptionsExt;
                options.mode(0o600);
            }

            match options.open(&path) {
                Ok(mut file) => {
                    return match file.write_all(content.as_bytes()) {
                        Ok(()) => Ok(CurlConfig(path)),
                        Err(e) => {
                            let _ = std::fs::remove_file(&path);
                            Err(HttpError(format!("could not write curl config: {e}")))
                        }
                    };
                }
                Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => continue,
                Err(e) => return Err(HttpError(format!("could not create curl config: {e}"))),
            }
        }
    }

    fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for CurlConfig {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.0);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_status_and_body_from_a_simple_response() {
        let response =
            parse_response("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n{\"a\":1}")
                .unwrap();
        assert_eq!(response.status, 200);
        assert_eq!(response.body, "{\"a\":1}");
        assert!(response.is_success());
    }

    #[test]
    fn takes_the_final_block_when_curl_reports_a_redirect() {
        let raw = "HTTP/1.1 301 Moved\r\nLocation: /next\r\n\r\nHTTP/1.1 200 OK\r\n\r\ndone";
        let response = parse_response(raw).unwrap();
        assert_eq!(response.status, 200);
        assert_eq!(response.body, "done");
    }

    #[test]
    fn keeps_a_non_success_status_as_a_response_not_an_error() {
        let response = parse_response("HTTP/1.1 401 Unauthorized\r\n\r\nnope").unwrap();
        assert_eq!(response.status, 401);
        assert!(!response.is_success());
        assert!(response.is_auth_failure());
    }

    #[test]
    fn a_body_with_a_blank_line_survives_intact() {
        let response = parse_response("HTTP/1.1 200 OK\r\n\r\nfirst\r\n\r\nsecond").unwrap();
        assert_eq!(response.body, "first\r\n\r\nsecond");
    }

    #[test]
    fn output_without_a_status_line_is_an_error() {
        assert!(parse_response("garbage without headers").is_err());
    }

    #[test]
    fn control_characters_cannot_splice_extra_headers() {
        let client = CurlClient::new();
        let smuggled = HttpRequest::get("https://example.com")
            .header("X-Test", "value\r\nAuthorization: Bearer stolen");
        assert!(client.send(&smuggled).is_err());

        let bad_url = HttpRequest::get("https://example.com/\nrogue");
        assert!(client.send(&bad_url).is_err());
    }

    #[test]
    fn config_file_is_removed_when_dropped() {
        let config = CurlConfig::write(&[("A".into(), "b".into())]).unwrap();
        let path = config.path().to_path_buf();
        assert!(path.exists());
        drop(config);
        assert!(!path.exists());
    }

    #[test]
    fn header_values_are_quoted_safely_for_the_config_format() {
        let config = CurlConfig::write(&[("Cookie".into(), r#"a"b\c"#.into())]).unwrap();
        let content = std::fs::read_to_string(config.path()).unwrap();
        assert!(
            content.contains(r#"header = "Cookie: a\"b\\c""#),
            "{content}"
        );
    }

    #[test]
    fn requests_build_the_headers_they_are_given() {
        let request = HttpRequest::get("https://example.com")
            .bearer("  token  ")
            .optional_header("ChatGPT-Account-Id", "  ")
            .optional_header("X-Real", " yes ");
        assert_eq!(
            request.headers,
            vec![
                ("Authorization".into(), "Bearer token".into()),
                ("X-Real".into(), "yes".into()),
            ]
        );
    }
}
