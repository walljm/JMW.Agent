//go:build linux

package discover

import "regexp"

// dockerUserBridgeRe matches docker user-defined bridge interface names,
// which are always "br-" followed by a 12-char hex network ID.
var dockerUserBridgeRe = regexp.MustCompile(`^br-[0-9a-f]{12}$`)

// podmanBridgeRe matches podman's default bridge naming conventions.
var podmanBridgeRe = regexp.MustCompile(`^(cni-podman[0-9]*|podman[0-9]+)$`)

// classifyBridge returns (vendor, kind) for an ARP-table interface name when
// it identifies a known container/VM bridge. Empty strings mean "not a
// container bridge — leave classification to the normal pipeline."
//
// We rely on naming conventions enforced by the runtimes themselves rather
// than poking at /sys (which would race with bridge teardown and require
// extra syscalls per sighting). These names are well-defined and unlikely
// to collide with real NICs:
//
//   - docker0           : Docker default bridge
//   - br-<12 hex>       : Docker user-defined network bridge
//   - cni-podmanN / podmanN : Podman bridges
//   - lxcbr*            : LXC default bridge
//   - virbr*            : libvirt NAT bridge (KVM/QEMU guests)
func classifyBridge(iface string) (vendor, kind string) {
	switch {
	case iface == "docker0" || dockerUserBridgeRe.MatchString(iface):
		return "Docker", "container"
	case podmanBridgeRe.MatchString(iface):
		return "Podman", "container"
	case len(iface) >= 5 && iface[:5] == "lxcbr":
		return "LXC", "container"
	case len(iface) >= 5 && iface[:5] == "virbr":
		return "libvirt", "vm"
	}
	return "", ""
}
