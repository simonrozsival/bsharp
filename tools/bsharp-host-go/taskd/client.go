// Package taskd is a minimal Go client for the bsharp-taskd Unix-socket
// protocol. It speaks the same JSON-framed protocol as the generated C#
// host's TaskRunner.
//
// Wire format:
//   - 4-byte little-endian length, then UTF-8 JSON payload.
//   - First frame after connect: HandshakeRequest -> HandshakeResponse.
//   - Subsequent frames: TaskInvocation -> TaskResult.
//
// Socket discovery mirrors DaemonPaths.cs:
//
//	${TMPDIR}/bsharp-${uid}/taskd-${version}-${sdkFingerprint}.sock
//
// The Go host shares the same daemon binary and socket as the C# host.
package taskd

import (
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

const (
	// ProtocolVersion must match TaskModel.cs's ProtocolVersion constant.
	ProtocolVersion = 2
	// DaemonVersion must match DaemonPaths.DaemonVersion.
	DaemonVersion = "1"
)

type HandshakeRequest struct {
	ProtocolVersion int    `json:"ProtocolVersion"`
	SdkFingerprint  string `json:"SdkFingerprint"`
	DaemonVersion   string `json:"DaemonVersion"`
}

type HandshakeResponse struct {
	ProtocolVersion int    `json:"ProtocolVersion"`
	DaemonVersion   string `json:"DaemonVersion"`
	Error           string `json:"Error,omitempty"`
}

// TaskInvocation mirrors Bsharp.Generated.TaskModel.TaskInvocation.
// Properties holds raw JSON values to preserve fidelity for replay/recorded
// traces; in a future fully-typed Go host this will be a richer type.
type TaskInvocation struct {
	TaskName     string                     `json:"TaskName"`
	TargetName   string                     `json:"TargetName,omitempty"`
	AssemblyPath string                     `json:"AssemblyPath"`
	TypeName     string                     `json:"TypeName"`
	OutputNames  []string                   `json:"OutputNames,omitempty"`
	Cwd          string                     `json:"Cwd,omitempty"`
	Properties   map[string]json.RawMessage `json:"Properties,omitempty"`
}

type TaskResult struct {
	Success bool                       `json:"Success"`
	Outputs map[string]json.RawMessage `json:"Outputs,omitempty"`
	Error   string                     `json:"Error,omitempty"`
}

// Client is a long-lived connection to bsharp-taskd. Not goroutine-safe
// (matches the per-build serial usage in the C# host).
type Client struct {
	conn net.Conn
	buf  [4]byte
}

// UserDir returns ${TMPDIR}/bsharp-${uid}, creating it with 0700 if missing.
func UserDir() string {
	tmp := os.TempDir()
	uid := strconv.Itoa(os.Getuid())
	dir := filepath.Join(tmp, "bsharp-"+uid)
	_ = os.MkdirAll(dir, 0o700)
	_ = os.Chmod(dir, 0o700)
	return dir
}

func SocketPath(sdkFingerprint string) string {
	return filepath.Join(UserDir(), fmt.Sprintf("taskd-%s-%s.sock", DaemonVersion, sdkFingerprint))
}

func PidPath(sdkFingerprint string) string {
	return filepath.Join(UserDir(), fmt.Sprintf("taskd-%s-%s.pid", DaemonVersion, sdkFingerprint))
}

// Connect dials the socket and performs the handshake. If the socket does
// not exist, it spawns the daemon (via daemonExe) and waits up to ~5 s for
// it to accept.
func Connect(daemonExe, sdkFingerprint string) (*Client, error) {
	sock := SocketPath(sdkFingerprint)
	conn, err := dialWithRetry(sock, 50*time.Millisecond)
	if err != nil {
		if daemonExe == "" {
			return nil, fmt.Errorf("bsharp-taskd not running at %s and no daemon binary supplied", sock)
		}
		if err := spawnDaemon(daemonExe, sdkFingerprint, sock); err != nil {
			return nil, err
		}
		conn, err = dialWithRetry(sock, 5*time.Second)
		if err != nil {
			return nil, fmt.Errorf("daemon spawned but socket not accepting: %w", err)
		}
	}
	c := &Client{conn: conn}
	if err := c.handshake(sdkFingerprint); err != nil {
		c.Close()
		return nil, err
	}
	return c, nil
}

func (c *Client) Close() error { return c.conn.Close() }

func (c *Client) handshake(sdkFingerprint string) error {
	req := HandshakeRequest{ProtocolVersion: ProtocolVersion, SdkFingerprint: sdkFingerprint, DaemonVersion: DaemonVersion}
	b, _ := json.Marshal(req)
	if err := c.writeFrame(b); err != nil {
		return err
	}
	resp, err := c.readFrame()
	if err != nil {
		return err
	}
	var hr HandshakeResponse
	if err := json.Unmarshal(resp, &hr); err != nil {
		return err
	}
	if hr.Error != "" {
		return errors.New("daemon refused handshake: " + hr.Error)
	}
	return nil
}

// Invoke serializes the invocation, sends it, and reads back the result.
func (c *Client) Invoke(inv *TaskInvocation) (*TaskResult, error) {
	b, err := json.Marshal(inv)
	if err != nil {
		return nil, err
	}
	if err := c.writeFrame(b); err != nil {
		return nil, err
	}
	respBytes, err := c.readFrame()
	if err != nil {
		return nil, err
	}
	var tr TaskResult
	if err := json.Unmarshal(respBytes, &tr); err != nil {
		return nil, err
	}
	return &tr, nil
}

// InvokeRaw sends a pre-serialized invocation frame and returns the raw
// result bytes. Useful for trace replay where re-marshalling is wasted work.
func (c *Client) InvokeRaw(reqJSON []byte) ([]byte, error) {
	if err := c.writeFrame(reqJSON); err != nil {
		return nil, err
	}
	return c.readFrame()
}

func (c *Client) writeFrame(payload []byte) error {
	binary.LittleEndian.PutUint32(c.buf[:], uint32(len(payload)))
	if _, err := c.conn.Write(c.buf[:]); err != nil {
		return err
	}
	_, err := c.conn.Write(payload)
	return err
}

func (c *Client) readFrame() ([]byte, error) {
	if _, err := io.ReadFull(c.conn, c.buf[:]); err != nil {
		return nil, err
	}
	n := binary.LittleEndian.Uint32(c.buf[:])
	if n > 64*1024*1024 {
		return nil, fmt.Errorf("frame too large: %d", n)
	}
	payload := make([]byte, n)
	if _, err := io.ReadFull(c.conn, payload); err != nil {
		return nil, err
	}
	return payload, nil
}

func dialWithRetry(sock string, total time.Duration) (net.Conn, error) {
	deadline := time.Now().Add(total)
	var lastErr error
	for {
		conn, err := net.Dial("unix", sock)
		if err == nil {
			return conn, nil
		}
		lastErr = err
		if time.Now().After(deadline) {
			return nil, lastErr
		}
		time.Sleep(20 * time.Millisecond)
	}
}

func spawnDaemon(daemonExe, sdkFingerprint, sock string) error {
	if _, err := os.Stat(daemonExe); err != nil {
		return fmt.Errorf("daemon binary not found at %s: %w", daemonExe, err)
	}
	logPath := strings.TrimSuffix(sock, ".sock") + ".log"
	// Match the C# launcher: detach via setsid + sh background. Stdin/out/err
	// are redirected to logfile so the daemon survives the spawning process.
	shell := fmt.Sprintf(
		`exec %s --sdk-fingerprint %s --socket %s </dev/null >>%s 2>>%s &`,
		shellQuote(daemonExe), shellQuote(sdkFingerprint), shellQuote(sock),
		shellQuote(logPath), shellQuote(logPath))
	cmd := exec.Command("/bin/sh", "-c", shell)
	cmd.SysProcAttr = sysProcAttrDetach()
	return cmd.Run()
}

func shellQuote(s string) string {
	if s == "" {
		return "''"
	}
	if strings.ContainsAny(s, " \t\n'\"\\$&|<>;()`*?[]{}~!#") {
		return "'" + strings.ReplaceAll(s, "'", `'\''`) + "'"
	}
	return s
}
