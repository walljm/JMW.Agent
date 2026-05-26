package discover

import (
	"net"

	"github.com/walljm/jmwagent/internal/shared/proto"
)

// NetworkContext resolves the current network context for the agent:
// gateway MAC (primary identity), local interface name, CIDR, and SSID
// (Wi-Fi only). Returns nil if the gateway cannot be determined.
func NetworkContext() *proto.NetworkContext {
	gwIP := defaultGatewayIP()
	if gwIP == "" {
		return nil
	}

	gwMAC := gatewayMAC(gwIP)
	if gwMAC == "" {
		return nil
	}

	iface, cidr := localInterfaceForGateway(gwIP)

	ctx := &proto.NetworkContext{
		GatewayMAC: gwMAC,
		Interface:  iface,
		CIDR:       cidr,
		SSID:       platformSSID(iface),
	}
	return ctx
}

// gatewayMAC looks up the MAC address for the given gateway IP by
// scanning the current ARP table. The gateway should already be in
// the ARP cache because we just did a multicast ping + ARP scan.
func gatewayMAC(gwIP string) string {
	entries := scanARP()
	for _, e := range entries {
		if e.IP == gwIP {
			return e.MAC
		}
	}
	return ""
}

// localInterfaceForGateway finds the local interface and CIDR used to
// reach the given gateway IP.
func localInterfaceForGateway(gwIP string) (ifaceName, cidr string) {
	gw := net.ParseIP(gwIP)
	if gw == nil {
		return "", ""
	}

	ifaces, err := net.Interfaces()
	if err != nil {
		return "", ""
	}

	for _, ifc := range ifaces {
		if ifc.Flags&net.FlagUp == 0 || ifc.Flags&net.FlagLoopback != 0 {
			continue
		}
		addrs, err := ifc.Addrs()
		if err != nil {
			continue
		}
		for _, a := range addrs {
			ipn, ok := a.(*net.IPNet)
			if !ok {
				continue
			}
			if ipn.Contains(gw) {
				// Return the network CIDR (not the host address).
				network := net.IPNet{
					IP:   ipn.IP.Mask(ipn.Mask),
					Mask: ipn.Mask,
				}
				return ifc.Name, network.String()
			}
		}
	}
	return "", ""
}
