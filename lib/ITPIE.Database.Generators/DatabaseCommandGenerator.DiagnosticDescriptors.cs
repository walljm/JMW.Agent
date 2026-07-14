using Microsoft.CodeAnalysis;

namespace ITPIE.Database.Generators;

public static class DiagnosticDescriptors
{
    //
    // 1000-1999: Method signature
    //

    public static DiagnosticDescriptor DatabaseCommandMethodMustBeInNullableContext { get; } = new(
        id: "ITPIEDBGEN1000",
        title: "Database command method must be declared in a nullable context",
        messageFormat: "Database command method must be declared in a nullable context",
        category: "DatabaseCommandGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static DiagnosticDescriptor DatabaseCommandMethodMustBeInStaticClass { get; } = new(
        id: "ITPIEDBGEN1001",
        title: "Database command method must be declared in a static class",
        messageFormat: "Database command method must be declared in a static class",
        category: "DatabaseCommandGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static DiagnosticDescriptor DatabaseCommandMethodMustBeInPartialClass { get; } = new(
        id: "ITPIEDBGEN1002",
        title: "Database command method must be declared in a partial class",
        messageFormat: "Database command method must be declared in a partial class",
        category: "DatabaseCommandGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static DiagnosticDescriptor DatabaseCommandMethodMustBePartial { get; } = new(
        id: "ITPIEDBGEN1003",
        title: "Database command method must be partial",
        messageFormat: "Database command method must be partial",
        category: "DatabaseCommandGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static DiagnosticDescriptor DatabaseCommandMethodMustBeStatic { get; } = new(
        id: "ITPIEDBGEN1004",
        title: "Database command method must be static",
        messageFormat: "Database command method must be static",
        category: "DatabaseCommandGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static DiagnosticDescriptor DatabaseCommandMethodMustNotBeGeneric { get; } = new(
        id: "ITPIEDBGEN1005",
        title: "Database command method must not be generic",
        messageFormat: "Database command method must not be generic",
        category: "DatabaseCommandGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static DiagnosticDescriptor DatabaseCommandMethodMustReturnIAsyncEnumerableOfType { get; } = new(
        id: "ITPIEDBGEN1006",
        title: "Database command method must return IAsyncEnumerable{T}",
        messageFormat: "Database command method must return IAsyncEnumerable{T}",
        category: "DatabaseCommandGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static DiagnosticDescriptor DatabaseCommandMethodConnectionParameterNotFound { get; } = new(
        id: "ITPIEDBGEN1007",
        title: "Database command method must declare an NpgsqlConnection parameter",
        messageFormat: "Database command method must declare a single NpgsqlConnection as the first parameter",
        category: "DatabaseCommandGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static DiagnosticDescriptor DatabaseCommandMethodConnectionParameterMustBeFirst { get; } = new(
        id: "ITPIEDBGEN1008",
        title: "Database command method must declare an NpgsqlConnection parameter as the first parameter",
        messageFormat: "Database command method must declare a single NpgsqlConnection as the first parameter",
        category: "DatabaseCommandGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static DiagnosticDescriptor DatabaseCommandMethodMultipleConnectionParametersFound { get; } = new(
        id: "ITPIEDBGEN1009",
        title: "Database command method must not declare multiple NpgsqlConnection parameters",
        messageFormat: "Database command method must declare a single NpgsqlConnection as the first parameter",
        category: "DatabaseCommandGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static DiagnosticDescriptor DatabaseCommandMethodCancellationTokenParameterNotFound { get; } = new(
        id: "ITPIEDBGEN1010",
        title: "Database command method must declare a CancellationToken parameter",
        messageFormat: "Database command method must declare a single CancellationToken as the last parameter",
        category: "DatabaseCommandGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static DiagnosticDescriptor DatabaseCommandMethodCancellationTokenParameterMustBeLast { get; } = new(
        id: "ITPIEDBGEN1011",
        title: "Database command method must declare a CancellationToken parameter as the last parameter",
        messageFormat: "Database command method must declare a single CancellationToken as the last parameter",
        category: "DatabaseCommandGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static DiagnosticDescriptor DatabaseCommandMethodMultipleCancellationTokenParametersFound { get; } = new(
        id: "ITPIEDBGEN1012",
        title: "Database command method must not declare multiple CancellationToken parameters",
        messageFormat: "Database command method must declare a single CancellationToken as the last parameter",
        category: "DatabaseCommandGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static DiagnosticDescriptor DatabaseCommandMethodParameterRefNotSupported { get; } = new(
        id: "ITPIEDBGEN1013",
        title: "Database command method cannot have ref, in, or out parameters",
        messageFormat: "Database command method cannot have ref, in, or out parameters",
        category: "DatabaseCommandGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    //
    // 2000-2999: Command text
    //

    public static DiagnosticDescriptor DatabaseCommandTextFileMustEndWithSql { get; } = new(
        id: "ITPIEDBGEN2001",
        title: "Database command text file extension must be '.sql'",
        messageFormat: "Database command text file extension must be '.sql': {0}",
        category: "DatabaseCommandGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static DiagnosticDescriptor DatabaseCommandTextFileNotFound { get; } = new(
        id: "ITPIEDBGEN2002",
        title: "Database command text file cannot be found",
        messageFormat: "Database command text file cannot be found: {0}",
        category: "DatabaseCommandGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static DiagnosticDescriptor DatabaseCommandTextMustNotBeNullOrWhitespace { get; } = new(
        id: "ITPIEDBGEN2003",
        title: "Database command text must not be null, empty, or contain only whitespace",
        messageFormat: "Database command text must not be null, empty, or contain only whitespace: {0}",
        category: "DatabaseCommandGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static DiagnosticDescriptor DatabaseCommandTextPlaceholderInvalidSequence { get; } = new(
        id: "ITPIEDBGEN2004",
        title: "Database command text placeholders must be a valid sequence",
        messageFormat: "Database command text placeholders must start at 1 and increment by 1 with no gaps",
        category: "DatabaseCommandGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    //
    // 3000-3999: Bind parameters & row description
    //

    public static DiagnosticDescriptor DatabaseCommandBindParameterNotSupported { get; } = new(
        id: "ITPIEDBGEN3001",
        title: "Database command bind parameter is not supported",
        messageFormat: "Database command bind parameter is not supported: {0}",
        category: "DatabaseCommandGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static DiagnosticDescriptor DatabaseCommandRowDescriptionNotSupported { get; } = new(
        id: "ITPIEDBGEN3002",
        title: "Database command row description is not supported",
        messageFormat: "Database command row description is not supported: {0}",
        category: "DatabaseCommandGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static DiagnosticDescriptor DatabaseCommandBindParametersCountMismatch { get; } = new(
        id: "ITPIEDBGEN3003",
        title: "Database command bind parameter count must equal command text placeholder count",
        messageFormat: "Database command bind parameter count must equal command text placeholder count: {0}",
        category: "DatabaseCommandGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );
}