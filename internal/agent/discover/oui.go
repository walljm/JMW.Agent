package discover

import "github.com/walljm/jmwagent/internal/shared/oui"

// ouiLookup returns a vendor name for a MAC address using the shared IEEE
// registry database. Returns "" if the MAC is malformed or no prefix matches.
func ouiLookup(mac string) string {
	return oui.Lookup(mac)
}
