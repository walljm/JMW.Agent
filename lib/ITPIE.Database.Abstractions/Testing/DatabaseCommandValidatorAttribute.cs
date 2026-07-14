using Npgsql;

namespace ITPIE.Database.Abstractions.Testing;

/// <summary>
/// Provides information to guide the production of a database command validator method.
/// </summary>
/// <remarks>This attribute should be used only by the generator.</remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class DatabaseCommandValidatorAttribute : Attribute { }

/// <summary>
/// Defines the signature for a database command validator method.
/// </summary>
public delegate Task DatabaseCommandValidator(NpgsqlConnection connection, CancellationToken cancellationToken);