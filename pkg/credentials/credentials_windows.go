//go:build windows

package credentials

import (
	"errors"
	"syscall"
	"unsafe"
)

const credTypeGeneric = 1

// winFiletime mirrors the Win32 FILETIME struct embedded in CREDENTIALW.
type winFiletime struct {
	LowDateTime  uint32
	HighDateTime uint32
}

// winCredential mirrors the Win32 CREDENTIALW struct returned by CredReadW.
// Field order and types must match exactly so Go's natural alignment
// reproduces the same layout the API expects.
type winCredential struct {
	Flags              uint32
	Type               uint32
	TargetName         *uint16
	Comment            *uint16
	LastWritten        winFiletime
	CredentialBlobSize uint32
	CredentialBlob     *byte
	Persist            uint32
	AttributeCount     uint32
	Attributes         uintptr
	TargetAlias        *uint16
	UserName           *uint16
}

var (
	advapi32      = syscall.NewLazyDLL("advapi32.dll")
	procCredReadW = advapi32.NewProc("CredReadW")
	procCredFree  = advapi32.NewProc("CredFree")
)

// readWindowsKeyringSecret reads a generic credential's blob from the current
// user's Windows Credential Manager store via CredReadW.
func readWindowsKeyringSecret(target string) ([]byte, error) {
	targetPtr, err := syscall.UTF16PtrFromString(target)
	if err != nil {
		return nil, err
	}

	var cred *winCredential
	ret, _, callErr := procCredReadW.Call(
		uintptr(unsafe.Pointer(targetPtr)),
		uintptr(credTypeGeneric),
		0,
		uintptr(unsafe.Pointer(&cred)),
	)
	if ret == 0 {
		return nil, callErr
	}
	defer procCredFree.Call(uintptr(unsafe.Pointer(cred)))

	if cred.CredentialBlob == nil || cred.CredentialBlobSize == 0 {
		return nil, errors.New("empty credential blob")
	}

	blob := unsafe.Slice(cred.CredentialBlob, cred.CredentialBlobSize)
	out := make([]byte, len(blob))
	copy(out, blob)
	return out, nil
}
