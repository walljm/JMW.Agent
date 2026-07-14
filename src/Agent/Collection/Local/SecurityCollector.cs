using System.Runtime.Versioning;
using System.Text.Json;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Agent.Collection.Local;

/// <summary>
/// Collects security posture facts.
/// Linux:   firewall (ufw/firewalld/nftables/iptables), LUKS, SecureBoot, TPM, SELinux, AppArmor.
/// macOS:   ALF firewall, FileVault, SIP, Gatekeeper.
/// Windows: Windows Firewall, Windows Defender, BitLocker, TPM, SecureBoot.
/// </summary>
public sealed class SecurityCollector : OsDispatchLocalCollector
{
    public override string Name => "security";

    // ── Linux ─────────────────────────────────────────────────────────────────

    protected override async Task CollectLinuxAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        string? selinux = TryReadFile("/sys/fs/selinux/enforce");
        if (selinux is not null)
        {
            facts.Add(
                Fact.Create(FactPaths.SecuritySeLinuxMode, [deviceId], selinux == "1" ? "enforcing" : "permissive")
            );
        }
        else if (File.Exists("/etc/selinux/config"))
        {
            facts.Add(Fact.Create(FactPaths.SecuritySeLinuxMode, [deviceId], "disabled"));
        }

        string? aa = await DetectAppArmorAsync(ct);
        if (aa is not null)
        {
            facts.Add(Fact.Create(FactPaths.SecurityAppArmor, [deviceId], aa));
        }

        (string? fwProvider, bool fwEnabled) = await DetectLinuxFirewallAsync(ct);
        if (fwProvider is not null)
        {
            facts.Add(Fact.Create(FactPaths.SecurityFirewallProvider, [deviceId], fwProvider));
            facts.Add(Fact.Create(FactPaths.SecurityFirewallEnabled, [deviceId], fwEnabled));
        }

        foreach (LuksVol vol in await DetectLuksAsync(ct))
        {
            facts.Add(Fact.Create(FactPaths.SecurityEncryptedVolumeDevice, [deviceId, vol.Device], vol.Device));
            facts.Add(Fact.Create(FactPaths.SecurityEncryptedVolumeMountpoint, [deviceId, vol.Device], vol.Mountpoint));
            facts.Add(Fact.Create(FactPaths.SecurityEncryptedVolumeType, [deviceId, vol.Device], vol.Type));
        }

        bool? sb = DetectLinuxSecureBoot();
        if (sb.HasValue)
        {
            facts.Add(Fact.Create(FactPaths.SecuritySecureBoot, [deviceId], sb.Value));
        }

        string tpmPath = "/sys/class/tpm/tpm0";
        facts.Add(Fact.Create(FactPaths.SecurityTpmPresent, [deviceId], Directory.Exists(tpmPath)));
        if (Directory.Exists(tpmPath))
        {
            string? ver = TryReadFile($"{tpmPath}/tpm_version_major");
            if (ver is not null)
            {
                facts.Add(Fact.Create(FactPaths.SecurityTpmVersion, [deviceId], ver + ".0"));
            }
        }
    }

    private static async Task<string?> DetectAppArmorAsync(CancellationToken ct)
    {
        // aa-status --enabled exits 0 when AppArmor is enabled; we only need the status.
        return await CollectorHelper.RunForExitCodeAsync("aa-status", "--enabled", ct) == 0 ? "enforce" : null;
    }

    private static async Task<(string? Provider, bool Enabled)> DetectLinuxFirewallAsync(
        CancellationToken ct
    )
    {
        if (CollectorHelper.BinaryExists("ufw"))
        {
            string out_ = await CollectorHelper.RunAsync("ufw", "status", ct);
            return ("ufw", out_.Contains("Status: active"));
        }

        if (CollectorHelper.BinaryExists("firewall-cmd"))
        {
            string out_ = await CollectorHelper.RunAsync("firewall-cmd", "--state", ct);
            return ("firewalld", out_.Trim() == "running");
        }

        if (CollectorHelper.BinaryExists("nft"))
        {
            string out_ = await CollectorHelper.RunAsync("nft", "list ruleset", ct);
            return ("nftables", !string.IsNullOrWhiteSpace(out_));
        }

        if (CollectorHelper.BinaryExists("iptables"))
        {
            string out_ = await CollectorHelper.RunAsync("iptables", "-S", ct);
            int nonDefault = out_.Split('\n')
                .Count(l => l.Length > 0 && !l.StartsWith("-P ", StringComparison.OrdinalIgnoreCase));
            return ("iptables", nonDefault > 0);
        }

        return (null, false);
    }

    private sealed record LuksVol(string Device, string Mountpoint, string Type);

    private static async Task<List<LuksVol>> DetectLuksAsync(CancellationToken ct)
    {
        List<LuksVol> vols = new();
        if (!CollectorHelper.BinaryExists("lsblk"))
        {
            return vols;
        }

        string json = await CollectorHelper.RunAsync("lsblk", "-J -o NAME,FSTYPE,MOUNTPOINT,TYPE", ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return vols;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            foreach (JsonElement dev in doc.RootElement.GetProperty("blockdevices").EnumerateArray())
            {
                CheckLuks(dev, vols);
                if (dev.TryGetProperty("children", out JsonElement kids))
                {
                    foreach (JsonElement kid in kids.EnumerateArray())
                    {
                        CheckLuks(kid, vols);
                    }
                }
            }
        }
        catch { }

        return vols;
    }

    private static void CheckLuks(JsonElement dev, List<LuksVol> vols)
    {
        if (dev.GetStr("fstype") != "crypto_LUKS")
        {
            return;
        }

        string name = "/dev/" + (dev.GetStr("name") ?? "");
        string mount = dev.GetStr("mountpoint") ?? "";
        vols.Add(new LuksVol(name, mount, "luks"));
    }

    private static bool? DetectLinuxSecureBoot()
    {
        try
        {
            string[] matches = Directory.GetFiles("/sys/firmware/efi/efivars", "SecureBoot-*");
            if (matches.Length == 0)
            {
                return null;
            }

            byte[] data = File.ReadAllBytes(matches[0]);
            return data.Length > 0 && data[^1] == 1;
        }
        catch { return null; }
    }

    // ── macOS ─────────────────────────────────────────────────────────────────

    protected override async Task CollectMacOsAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        // Application Layer Firewall
        string fw = await CollectorHelper.RunAsync(
            "/usr/libexec/ApplicationFirewall/socketfilterfw",
            "--getglobalstate",
            ct
        );
        if (!string.IsNullOrEmpty(fw))
        {
            facts.Add(Fact.Create(FactPaths.SecurityFirewallProvider, [deviceId], "alf"));
            facts.Add(Fact.Create(FactPaths.SecurityFirewallEnabled, [deviceId], fw.Contains("enabled")));
        }

        // FileVault
        string fv = await CollectorHelper.RunAsync("fdesetup", "status", ct);
        if (!string.IsNullOrEmpty(fv))
        {
            bool on = fv.Contains("FileVault is On");
            facts.Add(Fact.Create(FactPaths.SecurityEncryptedVolumeType, [deviceId, "/"], "filevault"));
            facts.Add(Fact.Create(FactPaths.SecurityEncryptedVolumeStatus, [deviceId, "/"], on ? "on" : "off"));
        }

        // System Integrity Protection
        string sip = await CollectorHelper.RunAsync("csrutil", "status", ct);
        if (!string.IsNullOrEmpty(sip))
        {
            string sipState = sip.Contains("enabled") ? "enabled"
                : sip.Contains("disabled") ? "disabled"
                : "unknown";
            facts.Add(Fact.Create(FactPaths.SecuritySip, [deviceId], sipState));
        }

        // Gatekeeper
        string gk = await CollectorHelper.RunAsync("spctl", "--status", ct);
        if (!string.IsNullOrEmpty(gk))
        {
            facts.Add(
                Fact.Create(FactPaths.SecurityGatekeeper, [deviceId], gk.Contains("enabled") ? "enabled" : "disabled")
            );
        }
    }

    // ── Windows ───────────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    protected override async Task CollectWindowsAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        await CollectWinFirewallAsync(deviceId, facts, ct);
        await CollectWinDefenderAsync(deviceId, facts, ct);
        await CollectWinBitLockerAsync(deviceId, facts, ct);
        await CollectWinTpmAsync(deviceId, facts, ct);
        await CollectWinSecureBootAsync(deviceId, facts, ct);
    }

    [SupportedOSPlatform("windows")]
    private static async Task CollectWinFirewallAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        List<FwProfile> rows = await CollectorHelper.RunPsJsonAsync<FwProfile>(
            "Get-NetFirewallProfile | Select-Object Name, Enabled | ConvertTo-Json -Compress",
            ct
        );

        bool allOn = rows.Count > 0;
        foreach (FwProfile r in rows)
        {
            string profileName = r.Name ?? "";
            facts.Add(
                Fact.Create(FactPaths.SecurityFirewallProfile, [deviceId, profileName], r.Enabled ? "on" : "off")
            );
            if (!r.Enabled)
            {
                allOn = false;
            }
        }

        if (rows.Count > 0)
        {
            facts.Add(Fact.Create(FactPaths.SecurityFirewallProvider, [deviceId], "windows"));
            facts.Add(Fact.Create(FactPaths.SecurityFirewallEnabled, [deviceId], allOn));
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task CollectWinDefenderAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        const string script = """
            try {
              $d = Get-MpComputerStatus -ErrorAction Stop
              [pscustomobject]@{
                AMEnabled        = $d.AMServiceEnabled
                RealtimeEnabled  = $d.RealTimeProtectionEnabled
                SignatureAge     = [int]$d.AntivirusSignatureAge
                SignatureVersion = $d.AntivirusSignatureVersion
              } | ConvertTo-Json -Compress
            } catch { '{}' }
            """;
        DefenderStatus? d = await CollectorHelper.RunPsJsonOneAsync<DefenderStatus>(script, ct);
        if (d is null)
        {
            return;
        }

        facts.Add(Fact.Create(FactPaths.SecurityDefenderEnabled, [deviceId], d.AMEnabled));
        facts.Add(Fact.Create(FactPaths.SecurityDefenderRealtimeProtected, [deviceId], d.RealtimeEnabled));
        facts.Add(Fact.Create(FactPaths.SecurityDefenderSignatureAgeDays, [deviceId], d.SignatureAge));
        facts.AddIfPresent(FactPaths.SecurityDefenderSignatureVersion, [deviceId], d.SignatureVersion);
    }

    [SupportedOSPlatform("windows")]
    private static async Task CollectWinBitLockerAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        const string script = """
            try {
              Get-BitLockerVolume -ErrorAction Stop |
              Select-Object MountPoint, ProtectionStatus |
              ConvertTo-Json -Compress
            } catch { '[]' }
            """;
        List<BitLockerVol> rows = await CollectorHelper.RunPsJsonAsync<BitLockerVol>(script, ct);
        foreach (BitLockerVol r in rows)
        {
            if (r.MountPoint is null)
            {
                continue;
            }

            facts.Add(Fact.Create(FactPaths.SecurityEncryptedVolumeType, [deviceId, r.MountPoint], "bitlocker"));
            facts.Add(
                Fact.Create(
                    FactPaths.SecurityEncryptedVolumeStatus,
                    [deviceId, r.MountPoint],
                    r.ProtectionStatus == 1 ? "on" : "off"
                )
            );
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task CollectWinTpmAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        const string script = """
            try {
              $t = Get-Tpm -ErrorAction Stop
              [pscustomobject]@{ Present = $t.TpmPresent } | ConvertTo-Json -Compress
            } catch { '{"Present":false}' }
            """;
        TpmData? t = await CollectorHelper.RunPsJsonOneAsync<TpmData>(script, ct);
        if (t is not null)
        {
            facts.Add(Fact.Create(FactPaths.SecurityTpmPresent, [deviceId], t.Present));
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task CollectWinSecureBootAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        string out_ = await CollectorHelper.RunPsAsync(
            "try { (Confirm-SecureBootUEFI).ToString().ToLower() } catch { 'unknown' }",
            ct
        );
        string result = out_.Trim();
        if (result is "true" or "false")
        {
            facts.Add(Fact.Create(FactPaths.SecuritySecureBoot, [deviceId], result == "true"));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? TryReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    private sealed class FwProfile
    {
        public string? Name { get; set; }
        public bool Enabled { get; set; }
    }

    private sealed class DefenderStatus
    {
        public bool AMEnabled { get; set; }
        public bool RealtimeEnabled { get; set; }
        public int SignatureAge { get; set; }
        public string? SignatureVersion { get; set; }
    }

    private sealed class BitLockerVol
    {
        public string? MountPoint { get; set; }
        public int ProtectionStatus { get; set; }
    }

    private sealed class TpmData
    {
        public bool Present { get; set; }
    }
}