using System.Diagnostics.CodeAnalysis;

namespace JMW.Discovery.Core;

/// <summary>
/// Strips NUL (U+0000) from fact text. Postgres text/jsonb cannot store NUL — one
/// occurrence aborts the whole ingest batch (raw text: SqlState 22021; embedded in a
/// JSON-serialized key_values value: SqlState 22P05, since JSON-encoding turns a raw
/// NUL into a backslash-u-0000 escape sequence, which Postgres's JSON parser also
/// rejects — stripping the already-serialized JSON text no longer finds a literal NUL
/// byte to remove, which is why this must run on the raw string before serialization).
/// Collector-derived strings (raw network banners, certificate fields, NetBIOS names,
/// TLS/mDNS/SSDP text) can carry it, so every <see cref="Fact" /> is sanitized at
/// construction (<see cref="Fact.Create(string,FactValue,DateTimeOffset?)" /> and
/// <see cref="FactValue.FromString" />) rather than trusting every producer or every
/// downstream write path individually. Allocates only when a NUL is actually present.
/// </summary>
public static class TextSanitizer
{
    [return: NotNullIfNotNull(nameof(value))]
    public static string? StripNul(string? value) =>
        value is null || value.IndexOf('\0', StringComparison.Ordinal) < 0
            ? value
            : value.Replace("\0", "", StringComparison.Ordinal);
}