using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace JMW.Discovery.Agent;

/// <summary>
/// Captures one page of the agent's recent log output on demand, from whichever source the host
/// actually has (docs/plans/agent-log-viewer.md §4.1):
/// <list type="bullet">
/// <item>native systemd → <c>journalctl -u jmw-agent</c>, which includes systemd's own lifecycle
/// lines (restart, OOM-kill, non-zero exit) the app can never log about itself;</item>
/// <item>everywhere else (Docker, dev, macOS, Windows) → the in-process <see cref="LogRingBuffer"/>,
/// the same content <c>docker logs</c> would show, captured without needing docker.sock.</item>
/// </list>
/// Each page is bounded by a line count and a byte ceiling, and carries an opaque
/// <c>NextBeforeToken</c> (a journald <c>__CURSOR</c> or a ring-buffer <c>Seq</c>) for "load older".
/// </summary>
internal sealed class AgentLogCollector
{
    private const string UnitName = "jmw-agent";

    // Backstop against a single pathological line blowing the page up. Compared against character
    // count — an approximation of bytes, which is all a backstop needs.
    private const int ByteCeiling = 128 * 1024;

    private readonly LogRingBuffer _buffer;

    public AgentLogCollector(LogRingBuffer buffer) => _buffer = buffer;

    public sealed record LogPage(string Source, bool Truncated, string Text, string? NextBeforeToken);

    public async Task<LogPage> CaptureAsync(int lines, string? before, CancellationToken ct)
    {
        if (IsSystemd())
        {
            LogPage? journal = await TryCaptureJournalAsync(lines, before, ct);
            if (journal is not null)
            {
                return journal;
            }
        }

        return CaptureBuffer(lines, before);
    }

    // Proof we're under a systemd instance AND journalctl is reachable. Both are required — the
    // native unit runs as root, so it can read the system journal (agent-log-viewer.md §6).
    private static bool IsSystemd() =>
        OperatingSystem.IsLinux()
     && Directory.Exists("/run/systemd/system")
     && BinaryExists("journalctl");

    private static bool BinaryExists(string name)
    {
        try
        {
            ProcessStartInfo psi = new("which", name)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process? p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }

            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<LogPage?> TryCaptureJournalAsync(int lines, string? before, CancellationToken ct)
    {
        try
        {
            ProcessStartInfo psi = new("journalctl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // -r = newest-first; over-fetch by one to detect whether older entries remain.
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(UnitName);
            psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add("--no-pager");
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add((lines + 1).ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("json");
            if (!string.IsNullOrEmpty(before))
            {
                // With -r, the cursor entry is included then older follow — we drop the anchor below.
                psi.ArgumentList.Add("--cursor");
                psi.ArgumentList.Add(before);
            }

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));

            using Process? proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            string stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            if (proc.ExitCode != 0)
            {
                return null;
            }

            List<(string Token, string Line)> ordered = new();
            foreach (string raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryParseJournalEntry(raw, out string cursor, out string line)
                 && (string.IsNullOrEmpty(before) || cursor != before))
                {
                    ordered.Add((cursor, line));
                }
            }

            (string text, string? nextBefore, bool truncated) = Paginate(ordered, lines);
            return new LogPage("journald", truncated, text, nextBefore);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // journalctl timed out — fall back to the buffer rather than failing the request.
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseJournalEntry(string rawJsonLine, out string cursor, out string line)
    {
        cursor = "";
        line = "";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJsonLine);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            cursor = root.TryGetProperty("__CURSOR", out JsonElement c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? ""
                : "";

            string ts = "";
            if (root.TryGetProperty("__REALTIME_TIMESTAMP", out JsonElement tsEl)
             && tsEl.ValueKind == JsonValueKind.String
             && long.TryParse(tsEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long micros))
            {
                ts = DateTimeOffset.FromUnixTimeMilliseconds(micros / 1000)
                    .UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            }

            string message = ReadMessage(root);
            line = ts.Length > 0 ? $"{ts} {message}" : message;
            return cursor.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    // journald MESSAGE is usually a string, but binary/non-UTF8 messages arrive as an array of
    // byte values. Decode both; anything else yields an empty message.
    private static string ReadMessage(JsonElement root)
    {
        if (!root.TryGetProperty("MESSAGE", out JsonElement msg))
        {
            return "";
        }

        if (msg.ValueKind == JsonValueKind.String)
        {
            return msg.GetString() ?? "";
        }

        if (msg.ValueKind == JsonValueKind.Array)
        {
            List<byte> bytes = new(msg.GetArrayLength());
            foreach (JsonElement b in msg.EnumerateArray())
            {
                if (b.ValueKind == JsonValueKind.Number && b.TryGetInt32(out int v) && v is >= 0 and <= 255)
                {
                    bytes.Add((byte)v);
                }
            }

            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        return "";
    }

    private LogPage CaptureBuffer(int lines, string? before)
    {
        long? beforeSeq = long.TryParse(before, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seq)
            ? seq
            : null;

        List<(string Token, string Line)> ordered = _buffer
            .Snapshot(beforeSeq, lines + 1)
            .Select(e => (e.Seq.ToString(CultureInfo.InvariantCulture), e.Line))
            .ToList();

        (string text, string? nextBefore, bool truncated) = Paginate(ordered, lines);
        return new LogPage("buffer", truncated, text, nextBefore);
    }

    // Takes a newest-first list over-fetched to at most (lines + 1) and produces the page text
    // plus the token to page older. NextBefore is null only when nothing older remains.
    private static (string Text, string? NextBefore, bool Truncated) Paginate(
        List<(string Token, string Line)> ordered,
        int lines
    )
    {
        StringBuilder sb = new();
        int taken = 0;
        bool truncated = false;
        string? lastToken = null;

        foreach ((string token, string line) in ordered)
        {
            if (taken >= lines)
            {
                break;
            }

            if (taken > 0 && sb.Length + 1 + line.Length > ByteCeiling)
            {
                truncated = true;
                break;
            }

            if (taken > 0)
            {
                sb.Append('\n');
            }

            sb.Append(line);
            lastToken = token;
            taken++;
        }

        bool hasOlder = ordered.Count > taken;
        return (sb.ToString(), hasOlder ? lastToken : null, truncated);
    }
}