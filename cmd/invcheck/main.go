package main

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"time"

	"github.com/walljm/jmwagent/internal/agent/collect"
)

func main() {
	t := time.Now()
	ctx := context.Background()
	inv := collect.Inventory(ctx, false)
	fmt.Fprintln(os.Stderr, "elapsed:", time.Since(t))
	b, _ := json.MarshalIndent(inv, "", "  ")
	fmt.Fprintln(os.Stderr, "bytes:", len(b))
	os.Stdout.Write(b)
}
