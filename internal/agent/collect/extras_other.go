//go:build !linux && !darwin && !windows

package collect

import (
	"context"

	"github.com/walljm/jmwagent/internal/shared/proto"
)

func collectFilesystems(ctx context.Context) []proto.FilesystemUsage { return nil }
func collectUpdates(ctx context.Context) *proto.UpdateStatus         { return nil }
func collectServices(ctx context.Context) []proto.ServiceStatus      { return nil }
func collectSecurity(ctx context.Context) *proto.SecurityPosture     { return nil }
func collectGPUs(ctx context.Context) []proto.GPU                    { return nil }
func collectChassis(ctx context.Context) *proto.ChassisInfo          { return nil }
func collectLocalUsers(ctx context.Context) []proto.LocalUser        { return nil }
func enrichDiskSMART(ctx context.Context, disks []proto.DiskDevice)  {}
