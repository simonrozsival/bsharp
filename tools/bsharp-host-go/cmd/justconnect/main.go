package main

import (
	"fmt"
	"github.com/simonrozsival/bsharp-host-go/taskd"
	"os"
)

func main() {
	c, err := taskd.Connect("/Users/simonrozsival/Projects/playground/bsharp/tools/bsharp/bin/Release/net11.0/osx-arm64/publish/bsharp-taskd", "49858abe90d0")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	c.Close()
}
