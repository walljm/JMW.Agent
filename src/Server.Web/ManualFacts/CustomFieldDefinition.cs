namespace JMW.Discovery.Server.ManualFacts;

/// <summary>
/// A row from custom_field_definitions — the schema for one operator-defined custom field.
/// Per-device VALUES are separate facts (Device[].Custom[].Value, keyed by <see cref="Slug" />).
/// </summary>
public sealed record CustomFieldDefinition(
    Guid Id,
    string Label,
    string Slug,
    string? TargetViewTitle,
    string? TargetViewGroup,
    bool IsNewView,
    DateTimeOffset CreatedAt,
    string CreatedBy
);
