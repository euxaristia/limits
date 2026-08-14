//! Making a probe CLI safe to spawn from an interactive terminal.
//!
//! The CLIs used to refresh expired tokens are agents and full-screen TUIs.
//! Given the user's console they will paint over the output or block on a
//! prompt; given the user's working directory they will inspect (and run
//! commands against) whatever project the user happens to be standing in. So
//! each one gets no stdin, a neutral directory, and its own console or process
//! group.

use std::process::{Command, Stdio};

/// Deny the child the terminal and the user's project directory. Output
/// redirection is left to the caller, so `Command::output` can still capture a
/// secret while a probe discards everything.
pub fn prepare(command: &mut Command) {
    command.stdin(Stdio::null());
    command.current_dir(std::env::temp_dir());
    detach(command);
}

#[cfg(windows)]
fn detach(command: &mut Command) {
    use std::os::windows::process::CommandExt;
    // CREATE_NO_WINDOW: the child gets its own hidden console, so it cannot
    // read the user's keyboard or draw over the terminal.
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    command.creation_flags(CREATE_NO_WINDOW);
}

#[cfg(unix)]
fn detach(command: &mut Command) {
    use std::os::unix::process::CommandExt;
    // Its own process group, so a background child that reads the terminal
    // gets SIGTTIN instead of stealing the user's keystrokes.
    command.process_group(0);
}

#[cfg(not(any(unix, windows)))]
fn detach(_command: &mut Command) {}
