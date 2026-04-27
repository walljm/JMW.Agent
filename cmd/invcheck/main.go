package main

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/walljm/jmwagent/internal/agent/collect"
)

func main() {
	t := time.Now()
	ctx := context.Background()
	inv := collect.Inventory(ctx, false)
	fmt.Println("elapsed:", time.Since(t))
	b, _ := json.Marshal(inv)
	fmt.Println("bytes:", len(b))
	fmt.Println("interfaces:")
	for _, i := range inv.Network.Interfaces {
		fmt.Printf("  %s mac=%s up=%v ipv4=%v\n", i.Name, i.MAC, i.IsUp, i.IPv4)
	}
}
