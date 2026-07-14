using System.Collections;
using System.Reflection;

namespace ITPIE.Database.Abstractions.Testing;

/// <summary>
/// Provides access to the database command validator methods as a test data source.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             <description>Scans all types in the assembly of the specified type <typeparamref name="T" />.</description>
///         </item>
///         <item>
///             <description>Finds all static methods within those types.</description>
///         </item>
///         <item>
///             <description>Checks if the methods have the <see cref="DatabaseCommandValidatorAttribute" /> applied.</description>
///         </item>
///         <item>
///             <description>Creates a delegate for each method that matches the criteria.</description>
///         </item>
///     </list>
/// </remarks>
/// <typeparam name="T">The type used to identify the assembly to scan for database command validator methods.</typeparam>
public sealed class DatabaseCommandValidatorDataSource<T> : IEnumerable<object[]>
{
    private static IEnumerable<object[]> GetData() => typeof(T).Assembly.GetTypes()
        .SelectMany(static t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        .Where(static m => m.GetCustomAttribute<DatabaseCommandValidatorAttribute>(false) is not null)
        .Select(static m =>
            {
                DatabaseCommandValidator validator =
                    (DatabaseCommandValidator)Delegate.CreateDelegate(typeof(DatabaseCommandValidator), m);
                return new object[]
                {
                    validator,
                };
            }
        );

    private readonly IEnumerable<object[]> _data = GetData();

    public IEnumerator<object[]> GetEnumerator() => _data.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}