//! Turning each provider's response into windows.
//!
//! Parsing is kept apart from fetching so every response shape can be tested
//! against a fixture without a network, a credential, or a subprocess.

pub mod antigravity;
pub mod claude;
pub mod codex;
pub mod copilot;
pub mod opencode;
