using Microsoft.CodeAnalysis;

namespace ITPIE.Database.Generators.Helpers;

/// <summary>
/// Caches the set of named type symbols known to be supported by Npgsql.
/// </summary>
internal sealed class KnownTypeSymbols
{
    public KnownTypeSymbols(Compilation compilation)
    {
        Compilation = compilation;
    }

    public Compilation Compilation { get; }

    public INamedTypeSymbol? ValueTupleType => GetOrResolveType(typeof(ValueTuple));
    public INamedTypeSymbol? CancellationTokenType => GetOrResolveType(typeof(CancellationToken));
    public INamedTypeSymbol? ListOfTypeType => GetOrResolveType(typeof(List<>));

    // ReSharper disable once InconsistentNaming
    public INamedTypeSymbol? IAsyncEnumerableOfTypeType
        => GetOrResolveType("System.Collections.Generic.IAsyncEnumerable`1");

    public INamedTypeSymbol? NpgsqlConnectionType => GetOrResolveType("Npgsql.NpgsqlConnection");

    private INamedTypeSymbol? GetOrResolveType(Type type)
        => GetOrResolveType(type.FullName ?? throw new InvalidOperationException());

    private INamedTypeSymbol? GetOrResolveType(string fullyQualifiedName)
        => Compilation.GetTypeByMetadataName(fullyQualifiedName);
}