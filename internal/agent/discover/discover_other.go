//go:build !linux && !darwin

package discover

func scanARP() []Sighting { return nil }
