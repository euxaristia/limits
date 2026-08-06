//go:build !windows

package credentials

import "errors"

// readWindowsKeyringSecret is unreachable outside Windows; loadAntigravityKeyringJSON
// only calls it when runtime.GOOS == "windows".
func readWindowsKeyringSecret(_ string) ([]byte, error) {
	return nil, errors.New("windows keyring lookup unsupported on this platform")
}
