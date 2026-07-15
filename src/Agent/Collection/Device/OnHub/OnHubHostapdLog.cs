using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

using JMW.Discovery.Agent.Collection.Device.OnHub.Proto;

namespace JMW.Discovery.Agent.Collection.Device.OnHub;

/// <summary>Most recent hostapd activity for one station, keyed by obscured MAC.</summary>
public sealed record OnHubHostapdActivity(
    DateTimeOffset? LastActiveAt, // most recent "has been active" beacon-report line
    string? LastActiveInterface, // e.g. "wlan-5000mhz" | "guest-2400mhz" — band + main/guest
    DateTimeOffset? LastRoamingAt, // most recent IAPP ADD-notify (mesh handoff)
    string? LastRoamingApIp // real LAN IP of the mesh AP that handled that handoff
);

/// <summary>
/// Parses <c>/var/log/messages</c> (field 2 <c>files</c>) for hostapd STA-activity and IAPP
/// roaming lines — the only *temporal* per-device signal in the diagnostic report; everything
/// else (station_state_updates, ap-show, etc.) is a single point-in-time snapshot. Verified
/// against a live capture (docs/scratch/deep-dive-2.md §1):
/// <code>
/// 2026-07-13T20:15:44.272924Z INFO hostapd[2513]: wlan-2400mhz: STA 187f889c4e6*      IEEE 802.11: Station 187f889c4e6*      has been active 6s ago
/// 2026-07-13T20:30:02.515930Z INFO hostapd[2513]: guest-5000mhz: STA 7286489dd01*      IAPP: Received IAPP ADD-notify (seq# 0) from 192.168.1.217:3517 (STA not found)
/// </code>
/// The obscured-MAC token is the same format used everywhere else in the report (real OUI,
/// masked device bytes, trailing '*') — directly joinable with <c>OnHubStations</c>'s station
/// list by string match, no reconstruction needed. Only ~11h of rolling history was present in
/// the capture examined — this is a short window, not a long archive.
/// The file's content is gzip-compressed in some captures and plain text in others (both were
/// observed); <see cref="DecodedContent" /> detects the gzip magic bytes and falls back to
/// plain UTF-8 otherwise. Both this file and its sibling <c>/var/log/net.log</c> contain
/// non-UTF8 bytes elsewhere in the log — <see cref="Encoding.UTF8" />'s replacement-on-invalid
/// behavior (not an exception) keeps parsing tolerant of that.
/// </summary>
public static class OnHubHostapdLog
{
    private const string LogPath = "/var/log/messages";

    private static readonly Regex StaActiveLine = new(
        @"^(?<ts>\S+) INFO hostapd\[\d+\]: (?<iface>\S+): STA (?<mac>\S+)\s+IEEE 802\.11: Station \S+\s+has been active",
        RegexOptions.Compiled
    );

    private static readonly Regex IappAddNotifyLine = new(
        @"^(?<ts>\S+) INFO hostapd\[\d+\]: (?<iface>\S+): STA (?<mac>\S+)\s+IAPP: Received IAPP ADD-notify \(seq# \d+\) from (?<ip>[0-9.]+):\d+",
        RegexOptions.Compiled
    );

    public static IReadOnlyDictionary<string, OnHubHostapdActivity> ExtractByObscuredMac(DiagnosticReport report)
    {
        Dictionary<string, OnHubHostapdActivity> byMac = new(StringComparer.Ordinal);

        foreach (Proto.File file in report.Files)
        {
            if (string.Equals(file.Path, LogPath, StringComparison.Ordinal) && DecodedContent(file) is { } text)
            {
                ParseLines(text, byMac);
            }
        }

        return byMac;
    }

    private static void ParseLines(string text, Dictionary<string, OnHubHostapdActivity> byMac)
    {
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            Match active = StaActiveLine.Match(line);
            if (active.Success)
            {
                UpdateActive(byMac, active);
                continue;
            }

            Match roaming = IappAddNotifyLine.Match(line);
            if (roaming.Success)
            {
                UpdateRoaming(byMac, roaming);
            }
        }
    }

    private static void UpdateActive(Dictionary<string, OnHubHostapdActivity> byMac, Match m)
    {
        if (OnHubStations.NormalizeObscuredMac(m.Groups["mac"].Value) is not { } mac
         || !TryParseTimestamp(m.Groups["ts"].Value, out DateTimeOffset ts))
        {
            return;
        }

        OnHubHostapdActivity existing = byMac.GetValueOrDefault(mac, new OnHubHostapdActivity(null, null, null, null));
        if (existing.LastActiveAt is null || ts > existing.LastActiveAt)
        {
            byMac[mac] = existing with { LastActiveAt = ts, LastActiveInterface = m.Groups["iface"].Value };
        }
    }

    private static void UpdateRoaming(Dictionary<string, OnHubHostapdActivity> byMac, Match m)
    {
        if (OnHubStations.NormalizeObscuredMac(m.Groups["mac"].Value) is not { } mac
         || !TryParseTimestamp(m.Groups["ts"].Value, out DateTimeOffset ts))
        {
            return;
        }

        OnHubHostapdActivity existing = byMac.GetValueOrDefault(mac, new OnHubHostapdActivity(null, null, null, null));
        if (existing.LastRoamingAt is null || ts > existing.LastRoamingAt)
        {
            byMac[mac] = existing with { LastRoamingAt = ts, LastRoamingApIp = m.Groups["ip"].Value };
        }
    }

    private static bool TryParseTimestamp(string s, out DateTimeOffset ts) =>
        DateTimeOffset.TryParse(
            s,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out ts
        );

    // Gzip-compressed in some captures, plain text in others — detect by magic bytes rather
    // than assuming either way, and degrade to "no data" on a corrupt/partial blob rather than
    // failing the whole collection cycle.
    private static string? DecodedContent(Proto.File file)
    {
        byte[] raw = file.Content.ToByteArray();
        if (raw.Length < 2 || raw[0] != 0x1f || raw[1] != 0x8b)
        {
            return Encoding.UTF8.GetString(raw);
        }

        try
        {
            using MemoryStream compressed = new(raw);
            using GZipStream gzip = new(compressed, CompressionMode.Decompress);
            using MemoryStream decompressed = new();
            gzip.CopyTo(decompressed);
            return Encoding.UTF8.GetString(decompressed.ToArray());
        }
        catch (IOException)
        {
            return null;
        }
    }
}