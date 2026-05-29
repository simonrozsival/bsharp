// Command replay replays a task trace captured by bsharp-taskd's
// BSHARP_TASKD_TRACE=<path> mode.
//
// Usage:
//
//	replay -trace /tmp/foo.trace.jsonl -sdk <fingerprint> -daemon <path>
//
// This is a research tool used to upper-bound how fast a Go-emitted host
// could be: it measures just the JSON-frame send/receive loop against the
// existing daemon, with zero MSBuild evaluation overhead.
package main

import (
	"bufio"
	"bytes"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"time"

	"github.com/simonrozsival/bsharp-host-go/taskd"
)

type traceEntry struct {
	Req  json.RawMessage `json:"req"`
	Resp json.RawMessage `json:"resp"`
}

func main() {
	tracePath := flag.String("trace", "", "path to .jsonl trace file")
	sdkFp := flag.String("sdk", "", "SDK fingerprint (matches socket name)")
	daemonPath := flag.String("daemon", "", "path to bsharp-taskd binary (used to spawn if not running)")
	verifyResults := flag.Bool("verify", false, "require Success=true on every reply")
	quiet := flag.Bool("quiet", false, "suppress per-task output")
	flag.Parse()

	if *tracePath == "" || *sdkFp == "" {
		fmt.Fprintln(os.Stderr, "usage: replay -trace <jsonl> -sdk <fingerprint> [-daemon <path>] [-verify] [-quiet]")
		os.Exit(2)
	}

	data, err := os.ReadFile(*tracePath)
	if err != nil {
		fatal("read trace: %v", err)
	}

	scanner := bufio.NewScanner(bytes.NewReader(data))
	scanner.Buffer(make([]byte, 0, 1<<20), 64<<20)
	var entries []traceEntry
	for scanner.Scan() {
		line := bytes.TrimSpace(scanner.Bytes())
		if len(line) == 0 {
			continue
		}
		var e traceEntry
		if err := json.Unmarshal(line, &e); err != nil {
			fatal("parse trace: %v", err)
		}
		entries = append(entries, e)
	}
	if err := scanner.Err(); err != nil {
		fatal("scan trace: %v", err)
	}
	if !*quiet {
		fmt.Printf("loaded %d task entries (%d bytes)\n", len(entries), len(data))
	}

	tConnect := time.Now()
	client, err := taskd.Connect(*daemonPath, *sdkFp)
	if err != nil {
		fatal("connect to daemon: %v", err)
	}
	defer client.Close()
	connectMs := time.Since(tConnect).Microseconds() / 1000

	tReplay := time.Now()
	var perTaskMicros []int64
	for i, e := range entries {
		t0 := time.Now()
		resp, err := client.InvokeRaw(e.Req)
		if err != nil {
			fatal("entry #%d: %v", i, err)
		}
		dt := time.Since(t0).Microseconds()
		perTaskMicros = append(perTaskMicros, dt)
		if *verifyResults {
			var tr taskd.TaskResult
			if err := json.Unmarshal(resp, &tr); err != nil {
				fatal("entry #%d: parse response: %v", i, err)
			}
			if !tr.Success {
				fatal("entry #%d: task %s failed: %s", i, taskName(e.Req), tr.Error)
			}
		}
		if !*quiet {
			fmt.Printf("  #%-3d %6.2fms %s\n", i, float64(dt)/1000.0, taskName(e.Req))
		}
	}
	totalMs := time.Since(tReplay).Microseconds() / 1000

	var sum int64
	for _, d := range perTaskMicros {
		sum += d
	}
	fmt.Fprintf(os.Stderr, "\nreplayed %d tasks in %d ms (connect=%d ms, sum-of-task-rtts=%d ms)\n",
		len(entries), totalMs, connectMs, sum/1000)
}

func taskName(req json.RawMessage) string {
	var hdr struct {
		TaskName string `json:"TaskName"`
	}
	_ = json.Unmarshal(req, &hdr)
	return hdr.TaskName
}

func fatal(format string, args ...any) {
	fmt.Fprintf(os.Stderr, "replay: "+format+"\n", args...)
	os.Exit(1)
}
