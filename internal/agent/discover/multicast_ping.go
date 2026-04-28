package discover

import (
	"net"
	"time"

	"golang.org/x/net/icmp"
	"golang.org/x/net/ipv4"
)

// multicastPingPrime sends an ICMP echo to 224.0.0.1 (the all-hosts
// multicast group) on every non-loopback IPv4 interface. The intent is
// to populate the kernel ARP table before scanARP() reads it: every
// reachable host that responds will send a unicast ICMP-echo-reply, and
// the kernel learns their MAC from the inbound Ethernet frame regardless
// of whether userspace consumes the reply.
//
// We don't read the replies — kernel-level ARP/neigh learning is what
// matters. We just give the responses a brief window to arrive before
// the caller proceeds.
//
// Requires raw ICMP capability (CAP_NET_RAW on Linux, root on macOS).
// On platforms / users where this fails, returns silently — discovery
// will still work using whatever ARP entries already exist.
func multicastPingPrime() {
	conn, err := icmp.ListenPacket("ip4:icmp", "0.0.0.0")
	if err != nil {
		return
	}
	defer conn.Close()

	pc := conn.IPv4PacketConn()
	_ = pc.SetMulticastTTL(1) // link-local only
	_ = pc.SetMulticastLoopback(false)

	echo := &icmp.Message{
		Type: ipv4.ICMPTypeEcho,
		Code: 0,
		Body: &icmp.Echo{
			ID:   0x4a4d, // "JM"
			Seq:  1,
			Data: []byte("jmw-agent-prime"),
		},
	}
	pkt, err := echo.Marshal(nil)
	if err != nil {
		return
	}

	dst := &net.IPAddr{IP: net.IPv4(224, 0, 0, 1)}
	ifs, err := net.Interfaces()
	if err != nil {
		return
	}
	sent := 0
	for _, ifc := range ifs {
		if ifc.Flags&net.FlagUp == 0 || ifc.Flags&net.FlagLoopback != 0 {
			continue
		}
		if ifc.Flags&net.FlagMulticast == 0 {
			continue
		}
		// Need at least one IPv4 address on this interface for the
		// reply path to make sense.
		addrs, err := ifc.Addrs()
		if err != nil {
			continue
		}
		hasV4 := false
		for _, a := range addrs {
			if ipn, ok := a.(*net.IPNet); ok && ipn.IP.To4() != nil && !ipn.IP.IsLoopback() {
				hasV4 = true
				break
			}
		}
		if !hasV4 {
			continue
		}
		ifc := ifc // copy for pointer
		if err := pc.SetMulticastInterface(&ifc); err != nil {
			continue
		}
		if _, err := conn.WriteTo(pkt, dst); err == nil {
			sent++
		}
	}
	if sent == 0 {
		return
	}
	// Drain replies (best-effort) so kernel learns MACs before we read
	// /proc/net/arp. The kernel learns from the L2 frame regardless of
	// whether we read; we still read to avoid leaving packets queued.
	_ = conn.SetReadDeadline(time.Now().Add(700 * time.Millisecond))
	buf := make([]byte, 1500)
	for {
		if _, _, err := conn.ReadFrom(buf); err != nil {
			break
		}
	}
}
