package credentials

import (
	"os/exec"
	"syscall"
)

// createNoWindow gives the child its own hidden console, so it cannot read the
// user's keyboard or paint over the terminal.
const createNoWindow = 0x08000000

func detachConsole(cmd *exec.Cmd) {
	if cmd.SysProcAttr == nil {
		cmd.SysProcAttr = &syscall.SysProcAttr{}
	}
	cmd.SysProcAttr.CreationFlags |= createNoWindow
}
