//go:build !windows

package credentials

import (
	"os/exec"
	"syscall"
)

// detachConsole puts the child in its own process group so it cannot read the
// terminal: a background process reading stdin gets SIGTTIN instead of stealing
// the user's keystrokes.
func detachConsole(cmd *exec.Cmd) {
	if cmd.SysProcAttr == nil {
		cmd.SysProcAttr = &syscall.SysProcAttr{}
	}
	cmd.SysProcAttr.Setpgid = true
}
