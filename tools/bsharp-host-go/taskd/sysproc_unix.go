//go:build unix

package taskd

import "syscall"

// sysProcAttrDetach detaches the spawned daemon from the launching process's
// session so SIGHUP (when the parent dies) doesn't kill it. Mirrors the
// setsid P/Invoke the C# launcher uses.
func sysProcAttrDetach() *syscall.SysProcAttr {
	return &syscall.SysProcAttr{Setsid: true}
}
