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
