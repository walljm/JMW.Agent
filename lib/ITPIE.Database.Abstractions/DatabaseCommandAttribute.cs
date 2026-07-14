namespace ITPIE.Database.Abstractions;

/// <summary>
/// Provides information to guide the production of a database command method.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///         Must be a static, partial, non-generic method in a static partial class. Compiler diagnostics may be emitted
///         when the generator cannot produce a method implementation informing you that a partial method implementation
///         is required (CS8795) or was not implemented correctly (CS0101, CS0751).
///         </item>
///         <item>
///         Must have an NpgsqlConnection as the first parameter. The method signature may be defined as an extension
///         method of the NpgsqlConnection type.
///         </item>
///         <item>
///         Must have a CancellationToken as the last parameter. If the method implementation requires it, this parameter
///         will be decorated for enumerator cancellation.
///         </item>
///         <item>
///         All other method parameters must be a single value (T), array of values (T[]), or list of values (List{T})
///         supported by Npgsql. No other types are supported, not a constructable named type nor any kind of tuple nor
///         any other collection concrete type nor interface.
///         </item>
///         <item>
///         Must return an IAsyncEnumerable{T}. The return type argument T must be a single value (R), array of values
///         (R[]), or list of values (List{R}) or a constructable named type or tuple where every parameter P is a single
///         value (P), array of values (P[]), or list of values (List{P}) supported by Npgsql.
///         </item>
///         <item>
///         The Task return type is not supported, but an empty constructable type is allowed.
///         </item>
///     </list>
/// </remarks>
/// <param name="commandTextFile">
/// The file containing the command text, see the remarks on the public property for usage details.
/// </param>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class DatabaseCommandAttribute(string? commandTextFile = null) : Attribute
{
    /// <summary>
    /// Gets the file containing the command text.
    /// </summary>
    /// <remarks>
    ///     <list type="bullet">
    ///         <item>
    ///         The command text file path must be relative to the file containing the method declaration.
    ///         </item>
    ///         <item>
    ///         The command text file extension must be '.sql'.
    ///         </item>
    ///         <item>
    ///         If the command text file path is not provided, the generator will search for one that matches the name of
    ///         the method name, except for the 'Async' suffix, if any. For example, given a method named
    ///         'SelectWidgetsAsync', the generator will search for a command text file named './SelectWidgets.sql'.
    ///         </item>
    ///         <item>
    ///         The command text file content must not be null, empty, or contain only whitespace.
    ///         </item>
    ///         <item>
    ///         The command text file content must not contain multiple commands separated by a semicolon, else
    ///         PostgreSQL will return an error at runtime (42601: cannot insert multiple commands into a prepared
    ///         statement).
    ///         </item>
    ///     </list>
    /// </remarks>
    public string? CommandTextFile { get; } = commandTextFile;
}