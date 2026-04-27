//go:build !linux && !darwin

package collect

import (
	"context"

	"github.com/walljm/jmwagent/internal/shared/proto"
)

func collectHardware(ctx context.Context) proto.HardwareInfo  { return proto.HardwareInfo{} }
func collectOS(ctx context.Context) proto.OSInfo              { return proto.OSInfo{Family: runtimeOS()} }
func collectDisks(ctx context.Context) []proto.DiskDevice     { return nil }
func collectRoutes(ctx context.Context) []proto.RouteEntry    { return nil }
func collectUsers(ctx context.Context) []proto.UserSession    { return nil }
func collectListening(ctx context.Context) []proto.ListeningPort { return nil }
func collectProcesses(ctx context.Context) []proto.ProcessSummary { return nil }
func collectReboots(ctx context.Context) []proto.BootRecord   { return nil }
func collectPackages(ctx context.Context) *proto.PackageInventory { return nil }

func ifaceEnrich(name string, ni *proto.NetInterface)         {}
func defaultGateways(ctx context.Context) (string, string)    { return "", "" }
