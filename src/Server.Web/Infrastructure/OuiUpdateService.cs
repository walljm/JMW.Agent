using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

using ITPIE.Migrations;

using JMW.Discovery.Server.Data;
using JMW.Discovery.Server.Queries;

using Npgsql;

using NpgsqlTypes;

namespace JMW.Discovery.Server.Infrastructure;

public sealed record OuiEntry(string Prefix, int Bits, string Vendor, string? Country);

public sealed record OuiUpdateResult(
    DateTimeOffset RanAt,
    TimeSpan Duration,
    int RecordCount,
    string VersionHash
);

/// <summary>
/// Downloads the four IEEE OUI registry CSV files, parses them, and stores the
/// results in Postgres. Exposes a cached version hash for the heartbeat endpoint
/// and a TriggerAsync method for the admin API.
/// </summary>
[SuppressMessage(
    "Reliability",
    "CA1001",
    Justification = "Dispose() is implemented — analyzer false positive with primary constructor syntax."
)]
public sealed partial class OuiUpdateService : IDisposable
{
    private readonly MigrationCompletedSignal _migrationSignal;
    private readonly ILogger<OuiUpdateService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NpgsqlDataSource _db;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OuiUpdateService(
        NpgsqlDataSource db,
        MigrationCompletedSignal migrationSignal,
        IHttpClientFactory httpClientFactory,
        ILogger<OuiUpdateService> logger
    )
    {
        _db = db;
        _migrationSignal = migrationSignal;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // Cached in-memory so heartbeat reads don't hit the DB every cycle.
    private volatile string _currentVersionHash = string.Empty;

    // IEEE URLs for all four OUI registries.
    // IAB (legacy individual address blocks, frozen 2014) is included because pre-2014
    // hardware with IAB-assigned MACs is still in the field. IAB carves 36-bit sub-blocks
    // from two IEEE-owned MA-L entries (e.g. 00-50-C2) in the oui.csv; MA-S/OUI36 uses
    // a different MA-L block (70-B3-D5), so the address spaces are distinct in practice.
    // A dedup pass after merging guards against any edge-case collisions in the source data.
    private static readonly (string Url, int Bits)[] IeeeSources =
    [
        ("https://standards-oui.ieee.org/oui/oui.csv", 24),
        ("https://standards-oui.ieee.org/oui28/mam.csv", 28),
        ("https://standards-oui.ieee.org/oui36/oui36.csv", 36),
        ("https://standards-oui.ieee.org/iab/iab.csv", 36),
    ];

    public string CurrentVersionHash => _currentVersionHash;

    /// <summary>
    /// Loads the current version hash from DB on startup. Called by the server
    /// after migrations complete so the heartbeat is accurate from first request.
    /// </summary>
    public async Task InitAsync(CancellationToken ct)
    {
        await _migrationSignal.Completed.WaitAsync(ct);

        try
        {
            await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
            (string VersionHash, DateTimeOffset UpdatedAt, long RecordCount) meta =
                await conn.GetOuiMetaAsync(ct).FirstOrDefaultAsync(ct);
            if (meta != default)
            {
                _currentVersionHash = meta.VersionHash;
                Log.Initialized(_logger, meta.RecordCount, meta.VersionHash);
            }

            // First startup (or an emptied table): download the IEEE OUI registry so
            // vendor/OUI resolution works without waiting for an admin to trigger it.
            if (meta == default || meta.RecordCount == 0)
            {
                Log.EmptyBootstrapping(_logger);
                await TriggerAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.InitFailed(_logger, ex);
        }
    }

    /// <summary>
    /// Triggers an OUI database update. Waits up to 30 seconds to acquire the
    /// update lock; returns null if an update is already running.
    /// </summary>
    public async Task<OuiUpdateResult?> TriggerAsync(CancellationToken ct = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await _gate.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            // Deliberately pass the caller's ct, not timeout.Token: the 30s budget
            // bounds only lock acquisition above. Once the lock is held the update
            // (download + bulk import) runs to completion under the caller's token;
            // the HTTP fetch is separately bounded by the "oui" HttpClient timeout.
            // ReSharper disable once PossiblyMistakenUseOfCancellationToken
            return await UpdateAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<OuiUpdateResult> UpdateAsync(CancellationToken ct)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        Log.UpdateStarted(_logger);

        HttpClient http = _httpClientFactory.CreateClient("oui");
        List<OuiEntry> entries = [];

        foreach ((string url, int bits) in IeeeSources)
        {
            List<OuiEntry> fetched = await FetchSourceAsync(http, url, bits, ct);
            entries.AddRange(fetched);
            Log.SourceFetched(_logger, url, fetched.Count);
        }

        // Dedup by (prefix, bits) — IAB and OUI36 share the 36-bit space and edge-case
        // collisions exist in the source data. COPY has no ON CONFLICT support, so this
        // must happen before the bulk import. First occurrence wins.
        entries = entries
            .GroupBy(e => (e.Prefix, e.Bits))
            .Select(g => g.First())
            .ToList();

        string versionHash = ComputeHash(entries);

        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
        await BulkReplaceAsync(conn, entries, versionHash, startedAt, ct);

        _currentVersionHash = versionHash;

        TimeSpan elapsed = DateTimeOffset.UtcNow - startedAt;
        Log.UpdateCompleted(_logger, entries.Count, elapsed);

        return new OuiUpdateResult(startedAt, elapsed, entries.Count, versionHash);
    }

    private static async Task<List<OuiEntry>> FetchSourceAsync(
        HttpClient http,
        string url,
        int bits,
        CancellationToken ct
    )
    {
        using HttpResponseMessage response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        string content = await response.Content.ReadAsStringAsync(ct);
        List<OuiEntry> result = [];

        // IEEE CSV format: Registry,Assignment,Organization Name,Organization Address
        // First line is a header row — skip it.
        bool first = true;
        foreach (string line in content.Split('\n'))
        {
            if (first)
            {
                first = false;
                continue;
            }

            ReadOnlySpan<char> span = line.AsSpan().TrimEnd();
            if (span.IsEmpty)
            {
                continue;
            }

            // Find the second comma (past Registry, then Assignment)
            int comma1 = span.IndexOf(',');
            if (comma1 < 0)
            {
                continue;
            }

            int comma2 = span[(comma1 + 1)..].IndexOf(',');
            if (comma2 < 0)
            {
                continue;
            }

            comma2 += comma1 + 1;

            // Assignment is between comma1 and comma2 — uppercase hex, no separators
            ReadOnlySpan<char> assignment = span[(comma1 + 1)..comma2].Trim();
            if (assignment.IsEmpty)
            {
                continue;
            }

            // Vendor name is the third CSV field. The IEEE registry RFC-4180-quotes
            // organization names that contain commas (e.g. "Cisco Systems, Inc"), so
            // parse quotes rather than splitting on the next comma — a naive split
            // truncates the name and leaves a dangling leading quote ("Cisco Systems).
            string vendor = ParseCsvField(span[(comma2 + 1)..], out ReadOnlySpan<char> afterVendor);
            if (vendor.Length == 0)
            {
                continue;
            }

            // Organization Address is the fourth (last) CSV field — a flattened,
            // single-line postal address. Only the country is extracted from it.
            string address = ParseCsvField(afterVendor, out _);
            string? country = ExtractCountryCode(address);

            string prefix = assignment.ToString().ToLowerInvariant();
            result.Add(new OuiEntry(prefix, bits, vendor, country));
        }

        return result;
    }

    // Extracts the first CSV field from <paramref name="input" />, honoring RFC-4180
    // double-quoting: a quoted field runs to its closing quote (with "" as an escaped
    // quote) and may itself contain commas; an unquoted field runs to the next comma.
    // <paramref name="remainder" /> is everything after that field's delimiting comma
    // (empty if this was the last field), so callers can chain field-by-field parsing.
    private static string ParseCsvField(ReadOnlySpan<char> input, out ReadOnlySpan<char> remainder)
    {
        ReadOnlySpan<char> rest = input.TrimStart();
        if (rest.IsEmpty)
        {
            remainder = ReadOnlySpan<char>.Empty;
            return string.Empty;
        }

        if (rest[0] != '"')
        {
            int comma = rest.IndexOf(',');
            if (comma < 0)
            {
                remainder = ReadOnlySpan<char>.Empty;
                return rest.Trim().ToString();
            }

            remainder = rest[(comma + 1)..];
            return rest[..comma].Trim().ToString();
        }

        StringBuilder sb = new();
        int i = 1;
        while (i < rest.Length)
        {
            char ch = rest[i];
            if (ch == '"')
            {
                // A doubled quote ("") is a single escaped quote within the field.
                if (i + 1 < rest.Length && rest[i + 1] == '"')
                {
                    sb.Append('"');
                    i += 2;
                    continue;
                }

                i++; // move past the closing quote
                break;
            }

            sb.Append(ch);
            i++;
        }

        int nextComma = rest[i..].IndexOf(',');
        remainder = nextComma < 0 ? ReadOnlySpan<char>.Empty : rest[(i + nextComma + 1)..];
        return sb.ToString().Trim();
    }

    // ISO 3166-1 alpha-2 codes, used to validate a country-code guess extracted from
    // the free-text "Organization Address" field (see ExtractCountryCode). Restricting
    // to real codes rejects two-letter tokens that are actually US state abbreviations,
    // military postal codes, or foreign-language connector words ("de", "la") that
    // happen to be all-uppercase in a fully-capitalized address.
    private static readonly HashSet<string> Iso3166Alpha2 = new(StringComparer.Ordinal)
    {
        "AD", "AE", "AF", "AG", "AI", "AL", "AM", "AO", "AQ", "AR", "AS", "AT", "AU", "AW", "AX", "AZ",
        "BA", "BB", "BD", "BE", "BF", "BG", "BH", "BI", "BJ", "BL", "BM", "BN", "BO", "BQ", "BR", "BS",
        "BT", "BV", "BW", "BY", "BZ", "CA", "CC", "CD", "CF", "CG", "CH", "CI", "CK", "CL", "CM", "CN",
        "CO", "CR", "CU", "CV", "CW", "CX", "CY", "CZ", "DE", "DJ", "DK", "DM", "DO", "DZ", "EC", "EE",
        "EG", "EH", "ER", "ES", "ET", "FI", "FJ", "FK", "FM", "FO", "FR", "GA", "GB", "GD", "GE", "GF",
        "GG", "GH", "GI", "GL", "GM", "GN", "GP", "GQ", "GR", "GS", "GT", "GU", "GW", "GY", "HK", "HM",
        "HN", "HR", "HT", "HU", "ID", "IE", "IL", "IM", "IN", "IO", "IQ", "IR", "IS", "IT", "JE", "JM",
        "JO", "JP", "KE", "KG", "KH", "KI", "KM", "KN", "KP", "KR", "KW", "KY", "KZ", "LA", "LB", "LC",
        "LI", "LK", "LR", "LS", "LT", "LU", "LV", "LY", "MA", "MC", "MD", "ME", "MF", "MG", "MH", "MK",
        "ML", "MM", "MN", "MO", "MP", "MQ", "MR", "MS", "MT", "MU", "MV", "MW", "MX", "MY", "MZ", "NA",
        "NC", "NE", "NF", "NG", "NI", "NL", "NO", "NP", "NR", "NU", "NZ", "OM", "PA", "PE", "PF", "PG",
        "PH", "PK", "PL", "PM", "PN", "PR", "PS", "PT", "PW", "PY", "QA", "RE", "RO", "RS", "RU", "RW",
        "SA", "SB", "SC", "SD", "SE", "SG", "SH", "SI", "SJ", "SK", "SL", "SM", "SN", "SO", "SR", "SS",
        "ST", "SV", "SX", "SY", "SZ", "TC", "TD", "TF", "TG", "TH", "TJ", "TK", "TL", "TM", "TN", "TO",
        "TR", "TT", "TV", "TW", "TZ", "UA", "UG", "UM", "US", "UY", "UZ", "VA", "VC", "VE", "VG", "VI",
        "VN", "VU", "WF", "WS", "XK", "YE", "YT", "ZA", "ZM", "ZW",
    };

    /// <summary>
    /// Best-effort extraction of an ISO 3166-1 alpha-2 country code from an IEEE
    /// "Organization Address" field, which flattens a multi-line postal address onto
    /// one line as "...City State CC Zip" (or "...City CC" with no zip). The country
    /// is therefore reliably the token immediately before the zip code, or the final
    /// token when there is no zip — except some national zip formats (e.g. Dutch
    /// "1032 LA") end in a letter suffix that coincidentally looks like a country
    /// code; that case is detected by checking for a real country immediately before
    /// the preceding digit run. Returns null when no confident match is found (empty
    /// address, or a legacy row that spells the country out as a word).
    /// </summary>
    internal static string? ExtractCountryCode(string address)
    {
        if (address.Length == 0)
        {
            return null;
        }

        string[] tokens = address.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            tokens[i] = tokens[i].TrimEnd(',');
        }

        if (tokens.Length == 0)
        {
            return null;
        }

        string last = tokens[^1];
        if (IsCountryCodeToken(last))
        {
            // Dutch-style split zip code ("<Country> <digits> <letters>"): only
            // reinterpret the trailing token as a zip suffix when a real country
            // sits immediately before the digit run; otherwise trust the direct hit.
            if (tokens.Length >= 3 && IsAllDigits(tokens[^2]) && IsCountryCodeToken(tokens[^3]))
            {
                return tokens[^3];
            }

            return last;
        }

        // The last token is the zip code itself (not a bare country code) — scan a
        // short window to its left for the country that precedes it.
        int windowStart = Math.Max(0, tokens.Length - 5);
        for (int i = tokens.Length - 2; i >= windowStart; i--)
        {
            if (IsCountryCodeToken(tokens[i]))
            {
                return tokens[i];
            }
        }

        return null;
    }

    private static bool IsCountryCodeToken(string token) =>
        token.Length == 2 && IsAllUpperAlpha(token) && Iso3166Alpha2.Contains(token);

    private static bool IsAllUpperAlpha(string s)
    {
        foreach (char c in s)
        {
            if (c < 'A' || c > 'Z')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllDigits(string s)
    {
        foreach (char c in s)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return s.Length > 0;
    }

    private static async Task BulkReplaceAsync(
        NpgsqlConnection conn,
        List<OuiEntry> entries,
        string versionHash,
        DateTimeOffset updatedAt,
        CancellationToken ct
    )
    {
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(ct);

        // Truncate and reload — simpler than upsert for a full replacement.
        await using (NpgsqlCommand truncate = conn.CreateCommand())
        {
            truncate.Transaction = tx;
            truncate.CommandText = "TRUNCATE oui_entries";
            await truncate.ExecuteNonQueryAsync(ct);
        }

        await using (NpgsqlBinaryImporter importer = await conn.BeginBinaryImportAsync(
            "COPY oui_entries (prefix, bits, vendor, country) FROM STDIN (FORMAT BINARY)",
            ct
        ))
        {
            foreach (OuiEntry entry in entries)
            {
                await importer.StartRowAsync(ct);
                await importer.WriteAsync(entry.Prefix, NpgsqlDbType.Text, ct);
                await importer.WriteAsync(entry.Bits, NpgsqlDbType.Integer, ct);
                await importer.WriteAsync(entry.Vendor, NpgsqlDbType.Text, ct);
                if (entry.Country is null)
                {
                    await importer.WriteNullAsync(ct);
                }
                else
                {
                    await importer.WriteAsync(entry.Country, NpgsqlDbType.Text, ct);
                }
            }

            await importer.CompleteAsync(ct);
        }

        // Upsert the metadata row.
        await using (NpgsqlCommand meta = conn.CreateCommand())
        {
            meta.Transaction = tx;
            meta.CommandText = """
                INSERT INTO oui_meta (id, updated_at, record_count, version_hash)
                VALUES (1, $1, $2, $3)
                ON CONFLICT (id) DO UPDATE
                    SET updated_at    = EXCLUDED.updated_at,
                        record_count  = EXCLUDED.record_count,
                        version_hash  = EXCLUDED.version_hash
                """;
            meta.Parameters.Add(Param.TimestampTz(updatedAt));
            meta.Parameters.Add(Param.Integer(entries.Count));
            meta.Parameters.Add(Param.Text(versionHash));
            await meta.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Streams all OUI entries from the DB as a CSV (prefix,bits,vendor,country), one per line.
    /// The caller is responsible for flushing and disposing the writer.
    /// </summary>
    public async Task WriteOuiCsvAsync(TextWriter writer, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT prefix, bits, vendor, country FROM oui_entries ORDER BY prefix, bits";

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string prefix = reader.GetString(0);
            int bits = reader.GetInt32(1);
            string vendor = reader.GetString(2);
            string country = reader.IsDBNull(3) ? "" : reader.GetString(3);
            await writer.WriteLineAsync($"{prefix},{bits},{vendor},{country}");
        }
    }

    private static string ComputeHash(List<OuiEntry> entries)
    {
        // Build a deterministic fingerprint: sort by (prefix, bits) then hash.
        IEnumerable<OuiEntry> sorted = entries.OrderBy(e => e.Prefix).ThenBy(e => e.Bits);

        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (OuiEntry entry in sorted)
        {
            sha.AppendData(Encoding.UTF8.GetBytes($"{entry.Prefix},{entry.Bits},{entry.Vendor},{entry.Country}\n"));
        }

        byte[] hash = sha.GetHashAndReset();
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    public void Dispose() => _gate.Dispose();

    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "OUI database initialized from DB: {RecordCount} entries, version {VersionHash}."
        )]
        internal static partial void Initialized(ILogger logger, long recordCount, string versionHash);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "OUI database init failed (no OUI data in DB yet, or DB error)."
        )]
        internal static partial void InitFailed(ILogger logger, Exception ex);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "OUI database is empty — downloading the IEEE registry on startup."
        )]
        internal static partial void EmptyBootstrapping(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "OUI database update started.")]
        internal static partial void UpdateStarted(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Fetched {Count} entries from {Url}.")]
        internal static partial void SourceFetched(ILogger logger, string url, int count);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "OUI database update complete: {RecordCount} entries in {Elapsed}."
        )]
        internal static partial void UpdateCompleted(ILogger logger, int recordCount, TimeSpan elapsed);
    }
}