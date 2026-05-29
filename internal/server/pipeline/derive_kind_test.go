package pipeline

import "testing"

func TestClassify(t *testing.T) {
	tests := []struct {
		name string
		sig  deriveSignals
		want string
	}{
		{
			name: "container sighting wins immediately",
			sig:  deriveSignals{sightingKinds: []string{"container"}},
			want: KindContainer,
		},
		{
			name: "ipp service classifies as printer",
			sig:  deriveSignals{mdnsServices: []string{"_ipp._tcp.local"}},
			want: KindPrinter,
		},
		{
			name: "googlecast classifies as streamer",
			sig:  deriveSignals{mdnsServices: []string{"_googlecast._tcp.local"}},
			want: KindStreamer,
		},
		{
			name: "airplay classifies as streamer",
			sig:  deriveSignals{mdnsServices: []string{"_airplay._tcp.local"}},
			want: KindStreamer,
		},
		{
			name: "homekit classifies as iot",
			sig:  deriveSignals{mdnsServices: []string{"_hap._tcp.local"}},
			want: KindIoT,
		},
		{
			name: "agent on laptop chassis",
			sig:  deriveSignals{hasAgent: true, chassisType: "laptop"},
			want: KindLaptop,
		},
		{
			name: "agent on server chassis",
			sig:  deriveSignals{hasAgent: true, chassisType: "rack-mount"},
			want: KindServer,
		},
		{
			name: "agent with unknown chassis defaults to server",
			sig:  deriveSignals{hasAgent: true, chassisType: ""},
			want: KindServer,
		},
		{
			name: "hostname router prefix",
			sig:  deriveSignals{hostnames: []string{"router-main"}},
			want: KindRouter,
		},
		{
			name: "hostname switch prefix",
			sig:  deriveSignals{hostnames: []string{"sw-core-1"}},
			want: KindSwitch,
		},
		{
			name: "hostname nas prefix",
			sig:  deriveSignals{hostnames: []string{"nas10"}},
			want: KindNAS,
		},
		{
			name: "synology vendor → nas",
			sig:  deriveSignals{systemVendor: "synology inc"},
			want: KindNAS,
		},
		{
			name: "ubiquiti vendor → router",
			sig:  deriveSignals{systemVendor: "ubiquiti networks"},
			want: KindRouter,
		},
		// Sighting kinds beyond container
		{
			name: "google-cast sighting → streamer",
			sig:  deriveSignals{sightingKinds: []string{"google-cast"}},
			want: KindStreamer,
		},
		{
			name: "printer sighting → printer",
			sig:  deriveSignals{sightingKinds: []string{"printer"}},
			want: KindPrinter,
		},
		{
			name: "domain-controller sighting → server",
			sig:  deriveSignals{sightingKinds: []string{"domain-controller"}},
			want: KindServer,
		},
		// Probe-based
		{
			name: "ipp probe → printer",
			sig:  deriveSignals{probeKeys: []string{"ipp"}},
			want: KindPrinter,
		},
		{
			name: "airplay probe → streamer",
			sig:  deriveSignals{probeKeys: []string{"airplay"}},
			want: KindStreamer,
		},
		{
			name: "eureka probe → streamer",
			sig:  deriveSignals{probeKeys: []string{"eureka"}},
			want: KindStreamer,
		},
		{
			name: "roku probe → streamer",
			sig:  deriveSignals{probeKeys: []string{"roku"}},
			want: KindStreamer,
		},
		{
			name: "ldap probe → server",
			sig:  deriveSignals{probeKeys: []string{"ldap"}},
			want: KindServer,
		},
		{
			name: "ssh_fp probe alone → server (last-resort)",
			sig:  deriveSignals{probeKeys: []string{"ssh_fp"}},
			want: KindServer,
		},
		{
			name: "ssh_fp probe does not override NAS hostname",
			sig:  deriveSignals{probeKeys: []string{"ssh_fp"}, hostnames: []string{"nas-01"}},
			want: KindNAS,
		},
		// mDNS additions
		{
			name: "_ssh._tcp → server",
			sig:  deriveSignals{mdnsServices: []string{"_ssh._tcp.local"}},
			want: KindServer,
		},
		{
			name: "_companion-link._tcp → mobile",
			sig:  deriveSignals{mdnsServices: []string{"_companion-link._tcp.local"}},
			want: KindMobile,
		},
		{
			name: "_appletv._tcp → tv",
			sig:  deriveSignals{mdnsServices: []string{"_appletv._tcp.local"}},
			want: KindTV,
		},
		// Hostname additions
		{
			name: "iphone hostname → mobile",
			sig:  deriveSignals{hostnames: []string{"johns-iphone"}},
			want: KindMobile,
		},
		{
			name: "ipad hostname → mobile",
			sig:  deriveSignals{hostnames: []string{"my-ipad-pro"}},
			want: KindMobile,
		},
		{
			name: "macbook hostname → laptop",
			sig:  deriveSignals{hostnames: []string{"jasons-macbook-pro"}},
			want: KindLaptop,
		},
		{
			name: "proxmox hostname → hypervisor",
			sig:  deriveSignals{hostnames: []string{"proxmox-01"}},
			want: KindHypervisor,
		},
		{
			name: "pve- hostname → hypervisor",
			sig:  deriveSignals{hostnames: []string{"pve-node1"}},
			want: KindHypervisor,
		},
		{
			name: "chromecast hostname → streamer",
			sig:  deriveSignals{hostnames: []string{"chromecast-livingroom"}},
			want: KindStreamer,
		},
		{
			name: "ring- hostname → camera",
			sig:  deriveSignals{hostnames: []string{"ring-front-door"}},
			want: KindCamera,
		},
		{
			name: "shelly- hostname → iot",
			sig:  deriveSignals{hostnames: []string{"shelly-plug-01"}},
			want: KindIoT,
		},
		{
			name: "fw- hostname → router",
			sig:  deriveSignals{hostnames: []string{"fw-office"}},
			want: KindRouter,
		},
		// Vendor additions
		{
			name: "dell vendor → workstation",
			sig:  deriveSignals{systemVendor: "dell inc"},
			want: KindWorkstation,
		},
		{
			name: "lenovo vendor → workstation",
			sig:  deriveSignals{systemVendor: "lenovo"},
			want: KindWorkstation,
		},
		{
			name: "lg electronics vendor → tv",
			sig:  deriveSignals{systemVendor: "lg electronics"},
			want: KindTV,
		},
		{
			name: "raspberry pi vendor → iot",
			sig:  deriveSignals{systemVendor: "raspberry pi foundation"},
			want: KindIoT,
		},
		{
			name: "HPE vendor → server not printer",
			sig:  deriveSignals{systemVendor: "hewlett packard enterprise"},
			want: KindServer,
		},
		{
			name: "phone hostname → mobile",
			sig:  deriveSignals{hostnames: []string{"walljmphone.home"}},
			want: KindMobile,
		},
		{
			name: "myphone hostname → mobile",
			sig:  deriveSignals{hostnames: []string{"myphone"}},
			want: KindMobile,
		},
		{
			name: "onhub hostname → router",
			sig:  deriveSignals{hostnames: []string{"google-onhub.home"}},
			want: KindRouter,
		},
		{
			name: "google-wifi hostname → router",
			sig:  deriveSignals{hostnames: []string{"google-wifi.home"}},
			want: KindRouter,
		},
		{
			name: "no signals → empty",
			sig:  deriveSignals{},
			want: "",
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := classify(&tt.sig)
			if got != tt.want {
				t.Fatalf("classify() = %q, want %q", got, tt.want)
			}
		})
	}
}
