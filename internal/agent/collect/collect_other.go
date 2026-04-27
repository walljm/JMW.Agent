//go:build !linux && !darwin

package collect

import "github.com/walljm/jmwagent/internal/shared/proto"

func uptimeSeconds() int64                                          { return 0 }
func loadAvg() (float64, float64, float64)                          { return 0, 0, 0 }
func cpuPercent() (float64, error)                                  { return 0, nil }
func memInfo() (uint64, uint64, error)                              { return 0, 0, nil }
func diskUsage() []proto.DiskSnapshot                               { return nil }
func ifaceStats(name string) (uint64, uint64, uint64, uint64, bool) { return 0, 0, 0, 0, false }
