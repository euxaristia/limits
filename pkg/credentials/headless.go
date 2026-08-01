package credentials

import (
	"os"
	"os/exec"
	"time"
)

// PrepareHeadless makes a child process safe to spawn from an interactive
// terminal. Probe CLIs are agents and TUIs: given the user's console they will
// draw over the output and block on a prompt, and given the user's working
// directory they will inspect (and run commands against) whatever project the
// user happens to be standing in.
func PrepareHeadless(cmd *exec.Cmd) {
	cmd.Stdin = nil
	cmd.Dir = os.TempDir()
	// A killed launcher shim can leave grandchildren holding the output pipes,
	// which makes Wait block forever after the timeout fires.
	cmd.WaitDelay = 2 * time.Second
	detachConsole(cmd)
}
