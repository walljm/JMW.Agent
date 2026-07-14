using System.Collections;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;

using Microsoft.CodeAnalysis;

namespace ITPIE.Database.Generators.Helpers;

/// <summary>
/// Caches the set of named type symbols known to be supported by Npgsql.
/// </summary>
internal sealed class NpgsqlTypeSymbols
{
    public NpgsqlTypeSymbols(Compilation compilation)
    {
        Compilation = compilation;

        AddTypeIfNotNull(BoolType);
        AddTypeIfNotNull(ByteType);
        AddTypeIfNotNull(CharType);
        AddTypeIfNotNull(DecimalType);
        AddTypeIfNotNull(DoubleType);
        AddTypeIfNotNull(FloatType);
        AddTypeIfNotNull(IntType);
        AddTypeIfNotNull(LongType);
        AddTypeIfNotNull(SbyteType);
        AddTypeIfNotNull(ShortType);
        AddTypeIfNotNull(UintType);

        AddTypeIfNotNull(BigIntegerType);
        AddTypeIfNotNull(BitArrayType);
        AddTypeIfNotNull(DateOnlyType);
        AddTypeIfNotNull(DateTimeOffsetType);
        AddTypeIfNotNull(GuidType);
        AddTypeIfNotNull(IPAddressType);
        AddTypeIfNotNull(PhysicalAddressType);
        AddTypeIfNotNull(StringType);
        AddTypeIfNotNull(TimeSpanType);

        // ITPIE Primitives
        AddTypeIfNotNull(MacAddressType);
        AddTypeIfNotNull(JsonElementType);

        void AddTypeIfNotNull(ITypeSymbol? type)
        {
            if (type != null)
            {
                SupportedTypes.Add(type);
            }
        }
    }

    public Compilation Compilation { get; }

    public HashSet<ITypeSymbol> SupportedTypes { get; } = new(SymbolEqualityComparer.Default);

    #region Primitives

    public INamedTypeSymbol? BoolType => GetOrResolveType(typeof(bool));
    public INamedTypeSymbol? ByteType => GetOrResolveType(typeof(byte));
    public INamedTypeSymbol? CharType => GetOrResolveType(typeof(char));
    public INamedTypeSymbol? DecimalType => GetOrResolveType(typeof(decimal));
    public INamedTypeSymbol? DoubleType => GetOrResolveType(typeof(double));
    public INamedTypeSymbol? FloatType => GetOrResolveType(typeof(float));
    public INamedTypeSymbol? IntType => GetOrResolveType(typeof(int));
    public INamedTypeSymbol? LongType => GetOrResolveType(typeof(long));
    public INamedTypeSymbol? SbyteType => GetOrResolveType(typeof(sbyte));
    public INamedTypeSymbol? ShortType => GetOrResolveType(typeof(short));
    public INamedTypeSymbol? UintType => GetOrResolveType(typeof(uint));

    #endregion

    public INamedTypeSymbol? BigIntegerType => GetOrResolveType(typeof(BigInteger));
    public INamedTypeSymbol? BitArrayType => GetOrResolveType(typeof(BitArray));
    public INamedTypeSymbol? DateOnlyType => GetOrResolveType("System.DateOnly");
    public INamedTypeSymbol? DateTimeOffsetType => GetOrResolveType(typeof(DateTimeOffset));
    public INamedTypeSymbol? GuidType => GetOrResolveType(typeof(Guid));
    public INamedTypeSymbol? IPAddressType => GetOrResolveType(typeof(IPAddress));
    public INamedTypeSymbol? PhysicalAddressType => GetOrResolveType(typeof(PhysicalAddress));
    public INamedTypeSymbol? StringType => GetOrResolveType(typeof(string));
    public INamedTypeSymbol? TimeSpanType => GetOrResolveType(typeof(TimeSpan));
    public INamedTypeSymbol? MacAddressType => GetOrResolveType("ITPIE.Primitives.Networking.MacAddress");
    public INamedTypeSymbol? JsonElementType => GetOrResolveType("System.Text.Json.JsonElement");

    private INamedTypeSymbol? GetOrResolveType(Type type)
        => GetOrResolveType(type.FullName ?? throw new InvalidOperationException());

    private INamedTypeSymbol? GetOrResolveType(string fullyQualifiedName)
        => Compilation.GetTypeByMetadataName(fullyQualifiedName);

    /// <summary>
    /// Gets the default value for the specified type symbol.
    /// </summary>
    /// <remarks>
    /// Used to construct bind parameter values for validation methods.
    /// <list type="bullet">
    ///     <item>If the type symbol is null, returns <c>"null"</c>.</item>
    ///     <item>If the type is a nullable type, it unwraps the underlying type and returns the default value for that type.</item>
    ///     <item>If the type is not recognized, it returns <c>"default"</c>.</item>
    /// </list>
    /// </remarks>
    /// <param name="typeSymbol"></param>
    /// <returns>The default value as a string.</returns>
    public string GetDefaultValue(ITypeSymbol? typeSymbol)
    {
        if (typeSymbol is null)
        {
            return "null";
        }

        if (IsNullableOfType(typeSymbol, out ITypeSymbol? typeArgumentSymbol))
        {
            return GetDefaultValue(typeArgumentSymbol);
        }

        return typeSymbol switch
        {
            { } t when SymbolEqualityComparer.Default.Equals(t, BoolType) => "false",
            { } t when SymbolEqualityComparer.Default.Equals(t, ByteType) => "0",
            { } t when SymbolEqualityComparer.Default.Equals(t, CharType) => "'\\0'",
            { } t when SymbolEqualityComparer.Default.Equals(t, DecimalType) => "0.0m",
            { } t when SymbolEqualityComparer.Default.Equals(t, DoubleType) => "0.0",
            { } t when SymbolEqualityComparer.Default.Equals(t, FloatType) => "0.0f",
            { } t when SymbolEqualityComparer.Default.Equals(t, IntType) => "0",
            { } t when SymbolEqualityComparer.Default.Equals(t, LongType) => "0L",
            { } t when SymbolEqualityComparer.Default.Equals(t, SbyteType) => "0",
            { } t when SymbolEqualityComparer.Default.Equals(t, ShortType) => "0",
            { } t when SymbolEqualityComparer.Default.Equals(t, UintType) => "0U",
            { } t when SymbolEqualityComparer.Default.Equals(t, BigIntegerType) => "BigInteger.Zero",
            { } t when SymbolEqualityComparer.Default.Equals(t, BitArrayType) =>
                "new global::System.Collections.BitArray(0)",
            { } t when SymbolEqualityComparer.Default.Equals(t, DateOnlyType) => "global::System.DateOnly.MinValue",
            { } t when SymbolEqualityComparer.Default.Equals(t, DateTimeOffsetType) =>
                "global::System.DateTimeOffset.MinValue",
            { } t when SymbolEqualityComparer.Default.Equals(t, GuidType) => "global::System.Guid.Empty",
            { } t when SymbolEqualityComparer.Default.Equals(t, IPAddressType) => "global::System.Net.IPAddress.None",
            { } t when SymbolEqualityComparer.Default.Equals(t, PhysicalAddressType) =>
                "global::System.Net.NetworkInformation.PhysicalAddress.None",
            { } t when SymbolEqualityComparer.Default.Equals(t, StringType) => "\"\"",
            { } t when SymbolEqualityComparer.Default.Equals(t, TimeSpanType) => "global::System.TimeSpan.Zero",
            { } t when SymbolEqualityComparer.Default.Equals(t, JsonElementType) =>
                "global::System.Text.Json.JsonDocument.Parse(\"{}\").RootElement",
            _ => "default",
        };
    }

    /// <summary>
    /// Determines if the specified type symbol is a nullable type.
    /// </summary>
    /// <param name="typeSymbol"></param>
    /// <param name="typeArgumentSymbol"></param>
    /// <returns>True if the type is a nullable type, otherwise false.</returns>
    private static bool IsNullableOfType(ITypeSymbol? typeSymbol, out ITypeSymbol? typeArgumentSymbol)
    {
        typeArgumentSymbol = null;

        if (typeSymbol is INamedTypeSymbol namedTypeSymbol
         && namedTypeSymbol.IsGenericType
         && namedTypeSymbol.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
        )
        {
            typeArgumentSymbol = namedTypeSymbol.TypeArguments.FirstOrDefault();
            return true;
        }

        return false;
    }
}