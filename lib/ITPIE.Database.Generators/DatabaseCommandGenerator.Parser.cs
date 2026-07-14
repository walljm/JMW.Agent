using System.Collections.Immutable;
using System.Text.RegularExpressions;

using ITPIE.Database.Generators.Helpers;
using ITPIE.Database.Generators.Model;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ITPIE.Database.Generators;

public sealed partial class DatabaseCommandGenerator
{
    internal sealed class Parser
    {
        internal const string DatabaseCommandAttributeFullName = "ITPIE.Database.Abstractions.DatabaseCommandAttribute";

        private readonly Compilation _compilation;
        private readonly NpgsqlTypeSymbols _npgsqlTypeSymbols;
        private readonly KnownTypeSymbols _knownTypeSymbols;
        private readonly Action<Diagnostic> _reportDiagnostic;
        private readonly CancellationToken _cancellationToken;

        public Parser(
            Compilation compilation,
            Action<Diagnostic> reportDiagnostic,
            CancellationToken cancellationToken = default
        )
        {
            _compilation = compilation;

            _npgsqlTypeSymbols = new(compilation);
            _knownTypeSymbols = new(compilation);

            _reportDiagnostic = reportDiagnostic;
            _cancellationToken = cancellationToken;
        }

        public IReadOnlyList<DatabaseCommandClass> GetDatabaseCommandClasses(
            IEnumerable<ClassDeclarationSyntax?> classDeclarations,
            IEnumerable<AdditionalText> texts
        )
        {
            INamedTypeSymbol? databaseCommandAttributeType =
                _compilation.GetTypeByMetadataName(DatabaseCommandAttributeFullName);
            List<DatabaseCommandClass> results = [];

            // group by syntax tree, since they are expensive
            foreach (IGrouping<SyntaxTree?, ClassDeclarationSyntax?>? group in classDeclarations.GroupBy(x
                => x?.SyntaxTree
            ))
            {
                SemanticModel? model = null;

                foreach (ClassDeclarationSyntax? classDeclaration in group)
                {
                    if (classDeclaration is null)
                    {
                        throw new InvalidOperationException();
                    }

                    _cancellationToken.ThrowIfCancellationRequested();

                    #region Database Command Class

                    //
                    // The incremental generator ensures that every class declaration delievered here has at least one
                    // method annotated with the marker attribute, we just need to find it.
                    //

                    DatabaseCommandClass c = new()
                    {
                        Name = $"{classDeclaration.Identifier}{classDeclaration.TypeParameterList}",
                        Modifiers = classDeclaration.Modifiers.ToString(),
                        Keyword = classDeclaration.Keyword.ValueText,
                    };

                    string fullyQualifiedIdentifier = $"{classDeclaration.Identifier}";

                    #region Database Command Class Namespace

                    //
                    // The command class may be defined in the global namespace or a block- or file-scoped namespace.
                    // We walk the parent syntax nodes looking for such a thing, if any.
                    //

                    bool hasNamespace = false;
                    SyntaxNode? potentialNamespaceParent = classDeclaration.Parent;
                    while (potentialNamespaceParent is not null and
                                                       not NamespaceDeclarationSyntax and
                                                       not FileScopedNamespaceDeclarationSyntax
                    )
                    {
                        potentialNamespaceParent = potentialNamespaceParent.Parent;
                    }

                    if (potentialNamespaceParent is BaseNamespaceDeclarationSyntax namespaceParentDeclaration)
                    {
                        c.Namespace = $"{namespaceParentDeclaration.Name}";
                        while (namespaceParentDeclaration.Parent is not null and
                                                                    BaseNamespaceDeclarationSyntax
                                                                    nextNamespaceParentDeclaration)
                        {
                            namespaceParentDeclaration = nextNamespaceParentDeclaration;
                            c.Namespace = $"{namespaceParentDeclaration.Name}.{c.Namespace}";
                        }

                        hasNamespace = true;
                    }

                    #endregion

                    #region Database Command Class Parents

                    //
                    // The command class may be arbitrarily nested in other reference types (class, struct, or record).
                    // We walk the parent syntax nodes until we find the outermost and then record them in reverse of
                    // that order.
                    //

                    DatabaseCommandClass currentClass = c;

                    TypeDeclarationSyntax? parentClassDeclaration = classDeclaration.Parent as TypeDeclarationSyntax;
                    while (parentClassDeclaration is not null)
                    {
                        if (parentClassDeclaration.Kind() is not (SyntaxKind.ClassDeclaration or
                                                                  SyntaxKind.StructDeclaration or
                                                                  SyntaxKind.RecordDeclaration or
                                                                  SyntaxKind.RecordStructDeclaration))
                        {
                            parentClassDeclaration = parentClassDeclaration.Parent as TypeDeclarationSyntax;
                            continue;
                        }

                        currentClass.Parent = new()
                        {
                            Name = $"{parentClassDeclaration.Identifier}{parentClassDeclaration.TypeParameterList}",
                            Namespace = currentClass.Namespace,
                            Modifiers = parentClassDeclaration.Modifiers.ToString(),
                            Keyword = parentClassDeclaration.Keyword.ValueText,
                        };

                        fullyQualifiedIdentifier = fullyQualifiedIdentifier.Insert(
                            0,
                            $"{parentClassDeclaration.Identifier}+"
                        );

                        currentClass = currentClass.Parent;
                        parentClassDeclaration = parentClassDeclaration.Parent as TypeDeclarationSyntax;
                    }

                    #endregion

                    if (hasNamespace)
                    {
                        fullyQualifiedIdentifier = fullyQualifiedIdentifier.Insert(0, $"{c.Namespace}.");
                    }

                    #endregion

                    model ??= _compilation.GetSemanticModel(classDeclaration.SyntaxTree);

                    foreach (MemberDeclarationSyntax memberDeclaration in classDeclaration.Members)
                    {
                        if (memberDeclaration is not MethodDeclarationSyntax methodDeclaration)
                        {
                            // not a method
                            continue;
                        }

                        foreach (AttributeListSyntax attributeList in methodDeclaration.AttributeLists)
                        {
                            foreach (AttributeSyntax attribute in attributeList.Attributes)
                            {
                                //
                                // Here we can search for the command attribute and ensure that all other invariants
                                // and constraints are met, else we can continue the search. Separate analyzers should
                                // ensure that incorrect signatures cause a compile failure, no need to emit
                                // diagnostics here.
                                //

                                if (model.GetSymbolInfo(attribute, _cancellationToken).Symbol is not IMethodSymbol
                                    attributeSymbol)
                                {
                                    // bad method attribute
                                    continue;
                                }

                                if (!SymbolEqualityComparer.Default.Equals(
                                    databaseCommandAttributeType,
                                    attributeSymbol.ContainingType
                                ))
                                {
                                    // wrong method attribute
                                    continue;
                                }

                                if (model.GetDeclaredSymbol(methodDeclaration, _cancellationToken) is not
                                    { } methodSymbol)
                                {
                                    // bad method
                                    continue;
                                }

                                if (model.GetTypeInfo(methodDeclaration.ReturnType, _cancellationToken).Type is not
                                    INamedTypeSymbol returnTypeSymbol)
                                {
                                    // bad return type
                                    continue;
                                }

                                NullableContext nullableContext = model.GetNullableContext(methodDeclaration.SpanStart);
                                if (!(nullableContext.HasFlag(NullableContext.Enabled)
                                     || nullableContext.HasFlag(NullableContext.ContextInherited))
                                )
                                {
                                    ReportDiagnostic(
                                        DiagnosticDescriptors.DatabaseCommandMethodMustBeInNullableContext,
                                        methodDeclaration.Identifier.GetLocation()
                                    );
                                }

                                #region Database Command Method

                                //
                                // We should reproduce the command method's signature as accurately as possible,
                                // including goofy bits like parameter qualifiers, even if another analyzer will fail
                                // those parts.
                                //

                                DatabaseCommandMethod m = new()
                                {
                                    Name = methodSymbol.Name,
                                    FullyQualifiedIdentifier = $"{fullyQualifiedIdentifier}.{methodSymbol.Name}",
                                    Modifiers = methodDeclaration.Modifiers.ToString(),
                                    ReturnType = ToDisplayString(methodSymbol.ReturnType),
                                    IsExtensionMethod = methodSymbol.IsExtensionMethod,
                                };

                                if (!classDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                                {
                                    ReportDiagnostic(
                                        DiagnosticDescriptors.DatabaseCommandMethodMustBeInPartialClass,
                                        methodDeclaration.Identifier.GetLocation()
                                    );
                                    m.HasErrors = true;
                                }

                                if (!classDeclaration.Modifiers.Any(SyntaxKind.StaticKeyword))
                                {
                                    ReportDiagnostic(
                                        DiagnosticDescriptors.DatabaseCommandMethodMustBeInStaticClass,
                                        methodDeclaration.Identifier.GetLocation()
                                    );
                                    m.HasErrors = true;
                                }

                                if (!methodSymbol.IsPartialDefinition)
                                {
                                    ReportDiagnostic(
                                        DiagnosticDescriptors.DatabaseCommandMethodMustBePartial,
                                        methodDeclaration.Identifier.GetLocation()
                                    );
                                    m.HasErrors = true;
                                }

                                if (!methodSymbol.IsStatic)
                                {
                                    ReportDiagnostic(
                                        DiagnosticDescriptors.DatabaseCommandMethodMustBeStatic,
                                        methodDeclaration.Identifier.GetLocation()
                                    );
                                    m.HasErrors = true;
                                }

                                if (methodSymbol.Arity != 0)
                                {
                                    ReportDiagnostic(
                                        DiagnosticDescriptors.DatabaseCommandMethodMustNotBeGeneric,
                                        methodDeclaration.Identifier.GetLocation()
                                    );
                                    m.HasErrors = true;
                                }

                                if (!SymbolEqualityComparer.Default.Equals(
                                    _knownTypeSymbols.IAsyncEnumerableOfTypeType,
                                    returnTypeSymbol.OriginalDefinition
                                ))
                                {
                                    ReportDiagnostic(
                                        DiagnosticDescriptors.DatabaseCommandMethodMustReturnIAsyncEnumerableOfType,
                                        methodDeclaration.ReturnType.GetLocation()
                                    );
                                    m.HasErrors = true;
                                }

                                if (returnTypeSymbol.TypeArguments.FirstOrDefault() is not { } rowDescriptionTypeSymbol)
                                {
                                    // bad result type
                                    continue;
                                }

                                #region Database Command Method Required Parameters

                                //
                                // Several of the command method's parameters are required and handled specially, in
                                // part because we need to reference them when producing the method's implementation.
                                //

                                int connectionParameterCount = 0;
                                int connectionParameterIndex = -1;
                                int cancellationTokenParameterCount = 0;
                                int cancellationTokenParameterIndex = -1;

                                for (int i = 0; i < methodSymbol.Parameters.Length; i++)
                                {
                                    IParameterSymbol methodParameterSymbol = methodSymbol.Parameters[i];

                                    if (methodParameterSymbol.RefKind != RefKind.None)
                                    {
                                        ReportDiagnostic(
                                            DiagnosticDescriptors.DatabaseCommandMethodParameterRefNotSupported,
                                            methodDeclaration.ParameterList.Parameters[i].GetLocation()
                                        );
                                        m.HasErrors = true;
                                    }

                                    string paramName = SyntaxFacts.GetKeywordKind(methodParameterSymbol.Name) switch
                                    {
                                        SyntaxKind.None => methodParameterSymbol.Name,
                                        _ => $"@{methodParameterSymbol.Name}",
                                    };

                                    bool isConnection = SymbolEqualityComparer.Default.Equals(
                                        _knownTypeSymbols.NpgsqlConnectionType,
                                        methodParameterSymbol.Type
                                    );
                                    bool isCancellationToken = SymbolEqualityComparer.Default.Equals(
                                        _knownTypeSymbols.CancellationTokenType,
                                        methodParameterSymbol.Type
                                    );

                                    if (isConnection)
                                    {
                                        connectionParameterCount++;
                                        connectionParameterIndex = i;
                                    }

                                    if (isCancellationToken)
                                    {
                                        cancellationTokenParameterCount++;
                                        cancellationTokenParameterIndex = i;
                                    }

                                    DatabaseCommandMethodParameter p = new()
                                    {
                                        Name = paramName,
                                        Type = ToDisplayString(methodParameterSymbol.Type),
                                        IsConnection = isConnection,
                                        IsCancellationToken = isCancellationToken,
                                    };

                                    m.Parameters.Add(p);

                                    #region Database Command Bind Parameters

                                    //
                                    // Bind parameters are found in the method parameter list, after removing the
                                    // connection and cancellation token parameters. Bind parameters must be a type
                                    // that Npgsql can handle directly, without a custom type converter. This includes
                                    // List{T} and T[] where T is an Npgsql supported type.
                                    //

                                    if (!p.IsBindParameter)
                                    {
                                        continue;
                                    }

                                    if (IsArrayOfNpgsqlSupportedType(
                                        methodParameterSymbol.Type,
                                        out ITypeSymbol? elementTypeSymbol
                                    ))
                                    {
                                        m.BindParameters.Add(
                                            new()
                                            {
                                                Name = p.Name,
                                                Type = p.Type,
                                                IsCollection = true,
                                                ValidateDefaultValue =
                                                    _npgsqlTypeSymbols.GetDefaultValue(elementTypeSymbol),
                                            }
                                        );
                                    }
                                    else if (IsListOfNpgsqlSupportedType(
                                        methodParameterSymbol.Type,
                                        out ITypeSymbol? typeArgumentSymbol
                                    ))
                                    {
                                        m.BindParameters.Add(
                                            new()
                                            {
                                                Name = p.Name,
                                                Type = p.Type,
                                                IsCollection = true,
                                                ValidateDefaultValue =
                                                    _npgsqlTypeSymbols.GetDefaultValue(typeArgumentSymbol),
                                            }
                                        );
                                    }
                                    else if (IsSingleNpgsqlSupportedType(methodParameterSymbol.Type))
                                    {
                                        m.BindParameters.Add(
                                            new()
                                            {
                                                Name = p.Name,
                                                Type = p.Type,
                                                IsNullableOfType = IsNullableOfType(methodParameterSymbol.Type),
                                                ValidateDefaultValue =
                                                    _npgsqlTypeSymbols.GetDefaultValue(methodParameterSymbol.Type),
                                            }
                                        );
                                    }
                                    else
                                    {
                                        ReportDiagnostic(
                                            DiagnosticDescriptors.DatabaseCommandBindParameterNotSupported,
                                            methodDeclaration.ParameterList.Parameters[i].GetLocation(),
                                            paramName
                                        );
                                        m.HasErrors = true;
                                    }

                                    #endregion
                                }

                                //
                                // The required parameters, NpgsqlConnection and CancellationToken, must appear exactly
                                // once and as the first and last parameters, respectively.
                                //

                                if (connectionParameterCount == 0)
                                {
                                    ReportDiagnostic(
                                        DiagnosticDescriptors.DatabaseCommandMethodConnectionParameterNotFound,
                                        methodDeclaration.Identifier.GetLocation()
                                    );
                                    m.HasErrors = true;
                                }
                                else if (connectionParameterCount == 1 && connectionParameterIndex != 0)
                                {
                                    ReportDiagnostic(
                                        DiagnosticDescriptors.DatabaseCommandMethodConnectionParameterMustBeFirst,
                                        methodDeclaration.Identifier.GetLocation()
                                    );
                                    m.HasErrors = true;
                                }
                                else if (connectionParameterCount > 1)
                                {
                                    ReportDiagnostic(
                                        DiagnosticDescriptors.DatabaseCommandMethodMultipleConnectionParametersFound,
                                        methodDeclaration.Identifier.GetLocation()
                                    );
                                    m.HasErrors = true;
                                }

                                if (cancellationTokenParameterCount == 0)
                                {
                                    ReportDiagnostic(
                                        DiagnosticDescriptors.DatabaseCommandMethodCancellationTokenParameterNotFound,
                                        methodDeclaration.Identifier.GetLocation()
                                    );
                                    m.HasErrors = true;
                                }
                                else if (cancellationTokenParameterCount == 1
                                 && cancellationTokenParameterIndex != methodSymbol.Parameters.Length - 1)
                                {
                                    ReportDiagnostic(
                                        DiagnosticDescriptors.DatabaseCommandMethodCancellationTokenParameterMustBeLast,
                                        methodDeclaration.Identifier.GetLocation()
                                    );
                                    m.HasErrors = true;
                                }
                                else if (cancellationTokenParameterCount > 1)
                                {
                                    ReportDiagnostic(
                                        DiagnosticDescriptors
                                            .DatabaseCommandMethodMultipleCancellationTokenParametersFound,
                                        methodDeclaration.Identifier.GetLocation()
                                    );
                                    m.HasErrors = true;
                                }

                                #endregion

                                #region Database Command Row Description

                                //
                                // The command result is the singular closed generic type parameter argument in the
                                // method return type. At this time, we support only IAsyncEnumerable{TResult}. We'll
                                // recursively check that TResult is either A) a type that can be read directly by
                                // Npgsql or a tuple containing only parameters that can be read directly by Npgsql.
                                //

                                m.Result.Type = ToDisplayString(rowDescriptionTypeSymbol);

                                if (IsArrayOfNpgsqlSupportedType(rowDescriptionTypeSymbol, out _)
                                 || IsListOfNpgsqlSupportedType(rowDescriptionTypeSymbol, out _)
                                 || IsSingleNpgsqlSupportedType(rowDescriptionTypeSymbol))
                                {
                                    m.Result.IsSingleField = true;
                                    m.Result.Parameters.Add(
                                        new()
                                        {
                                            Type = ToDisplayString(rowDescriptionTypeSymbol),
                                            HasNullableAnnotation =
                                                rowDescriptionTypeSymbol.NullableAnnotation
                                             == NullableAnnotation.Annotated,
                                            IsValueType = rowDescriptionTypeSymbol.IsValueType,
                                        }
                                    );
                                }
                                else if (IsValueTuple(
                                    rowDescriptionTypeSymbol,
                                    out ImmutableArray<IFieldSymbol> tupleElements
                                ))
                                {
                                    m.Result.IsValueTuple = true;
                                    foreach (IFieldSymbol fieldSymbol in tupleElements)
                                    {
                                        if (IsArrayOfNpgsqlSupportedType(fieldSymbol.Type, out _)
                                         || IsListOfNpgsqlSupportedType(fieldSymbol.Type, out _)
                                         || IsSingleNpgsqlSupportedType(fieldSymbol.Type))
                                        {
                                            m.Result.Parameters.Add(
                                                new()
                                                {
                                                    Name = fieldSymbol.Name,
                                                    Type = ToDisplayString(fieldSymbol.Type),
                                                    HasNullableAnnotation =
                                                        fieldSymbol.NullableAnnotation == NullableAnnotation.Annotated,
                                                    IsValueType = fieldSymbol.Type.IsValueType,
                                                }
                                            );
                                        }
                                        else
                                        {
                                            ReportDiagnostic(
                                                DiagnosticDescriptors.DatabaseCommandRowDescriptionNotSupported,
                                                (methodDeclaration.ReturnType as GenericNameSyntax)?.TypeArgumentList
                                                .Arguments[0]
                                                .GetLocation()
                                             ?? methodDeclaration.ReturnType.GetLocation(),
                                                fieldSymbol.Name
                                            );
                                            m.HasErrors = true;

                                            break;
                                        }
                                    }
                                }
                                else if (IsConstructableType(
                                    rowDescriptionTypeSymbol,
                                    out ImmutableArray<IParameterSymbol> constructorParameterSymbols
                                ))
                                {
                                    foreach (IParameterSymbol parameterSymbol in constructorParameterSymbols)
                                    {
                                        if (IsArrayOfNpgsqlSupportedType(parameterSymbol.Type, out _)
                                         || IsListOfNpgsqlSupportedType(parameterSymbol.Type, out _)
                                         || IsSingleNpgsqlSupportedType(parameterSymbol.Type))
                                        {
                                            m.Result.Parameters.Add(
                                                new()
                                                {
                                                    Name = parameterSymbol.Name,
                                                    Type = ToDisplayString(parameterSymbol.Type),
                                                    HasNullableAnnotation =
                                                        parameterSymbol.NullableAnnotation
                                                     == NullableAnnotation.Annotated,
                                                    IsValueType = parameterSymbol.Type.IsValueType,
                                                }
                                            );
                                        }
                                        else
                                        {
                                            ReportDiagnostic(
                                                DiagnosticDescriptors.DatabaseCommandRowDescriptionNotSupported,
                                                (methodDeclaration.ReturnType as GenericNameSyntax)?.TypeArgumentList
                                                .Arguments[0]
                                                .GetLocation()
                                             ?? methodDeclaration.ReturnType.GetLocation(),
                                                parameterSymbol.Name
                                            );
                                            m.HasErrors = true;

                                            break;
                                        }
                                    }
                                }
                                else
                                {
                                    ReportDiagnostic(
                                        DiagnosticDescriptors.DatabaseCommandRowDescriptionNotSupported,
                                        (methodDeclaration.ReturnType as GenericNameSyntax)?.TypeArgumentList
                                        .Arguments[0]
                                        .GetLocation()
                                     ?? methodDeclaration.ReturnType.GetLocation(),
                                        rowDescriptionTypeSymbol.Name
                                    );
                                    m.HasErrors = true;
                                }

                                #endregion

                                #region Database Command Text

                                //
                                // The command attribute's command text file parameter is optional. If it is null or
                                // whitespace, we'll construct a file path matching the method name, minus the "Async"
                                // suffix, if any. All file paths are considered relative to the file path of the class
                                // declaration.
                                //

                                AttributeArgumentSyntax? commandTextFileArgumentSyntax =
                                    attribute.ArgumentList?.Arguments.FirstOrDefault();
                                string? commandTextFileName =
                                    (commandTextFileArgumentSyntax?.Expression as LiteralExpressionSyntax)?.Token
                                    .ValueText;

                                if (string.IsNullOrWhiteSpace(commandTextFileName))
                                {
                                    commandTextFileName = methodSymbol.Name;

                                    if (commandTextFileName.EndsWith("Async", StringComparison.OrdinalIgnoreCase))
                                    {
                                        commandTextFileName = commandTextFileName.Substring(
                                            0,
                                            commandTextFileName.Length - "Async".Length
                                        );
                                    }

                                    commandTextFileName += ".sql";
                                }

                                string commandTextFilePath = Path.Combine(
                                    Path.GetDirectoryName(classDeclaration.SyntaxTree.FilePath)
                                 ?? throw new NullReferenceException(),
                                    commandTextFileName
                                );
                                AdditionalText? commandTextFile = texts.FirstOrDefault(text
                                    => Path.GetFullPath(text.Path) == Path.GetFullPath(commandTextFilePath)
                                );
                                string? commandText = commandTextFile?.GetText(_cancellationToken)?.ToString();

                                if (!commandTextFilePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                                {
                                    ReportDiagnostic(
                                        DiagnosticDescriptors.DatabaseCommandTextFileMustEndWithSql,
                                        commandTextFileArgumentSyntax?.GetLocation() ?? attribute.GetLocation(),
                                        commandTextFilePath
                                    );
                                    m.HasErrors = true;
                                }
                                else if (commandTextFile is null)
                                {
                                    ReportDiagnostic(
                                        DiagnosticDescriptors.DatabaseCommandTextFileNotFound,
                                        commandTextFileArgumentSyntax?.GetLocation() ?? attribute.GetLocation(),
                                        commandTextFilePath
                                    );
                                    m.HasErrors = true;
                                }
                                else if (string.IsNullOrWhiteSpace(commandText))
                                {
                                    ReportDiagnostic(
                                        DiagnosticDescriptors.DatabaseCommandTextMustNotBeNullOrWhitespace,
                                        commandTextFileArgumentSyntax?.GetLocation() ?? attribute.GetLocation(),
                                        commandTextFilePath
                                    );
                                    m.HasErrors = true;
                                }
                                else
                                {
                                    m.CommandText.Value = commandText ?? throw new InvalidOperationException();
                                    m.CommandText.Path = commandTextFilePath;
                                }

                                #region Database Command Text Placeholders

                                if (!string.IsNullOrWhiteSpace(commandText))
                                {
                                    // Extract positional placeholders from the command text
                                    MatchCollection placeholderMatches = Regex.Matches(commandText, @"\$\d+");
                                    HashSet<int> placeholderSet = [];

                                    foreach (Match match in placeholderMatches)
                                    {
                                        if (int.TryParse(match.Value.Substring(1), out int placeholderIndex))
                                        {
                                            placeholderSet.Add(placeholderIndex);
                                        }
                                    }

                                    // Check if placeholders start with 1 and have no gaps
                                    List<int> orderedPlaceholders = [.. placeholderSet.OrderBy(x => x)];
                                    for (int i = 0; i < orderedPlaceholders.Count; i++)
                                    {
                                        if (orderedPlaceholders[i] != i + 1)
                                        {
                                            ReportDiagnostic(
                                                DiagnosticDescriptors.DatabaseCommandTextPlaceholderInvalidSequence,
                                                commandTextFileArgumentSyntax?.GetLocation() ?? attribute.GetLocation()
                                            );
                                            m.HasErrors = true;
                                            break;
                                        }
                                    }

                                    // Ensure the count matches the bind parameters
                                    if (orderedPlaceholders.Count != m.BindParameters.Count)
                                    {
                                        ReportDiagnostic(
                                            DiagnosticDescriptors.DatabaseCommandBindParametersCountMismatch,
                                            methodDeclaration.Identifier.GetLocation(),
                                            orderedPlaceholders.Count
                                        );
                                        m.HasErrors = true;
                                    }
                                }

                                #endregion

                                #endregion

                                if (!m.HasErrors)
                                {
                                    // skip method generation
                                    c.Methods.Add(m);
                                    m.Parent = c;
                                }

                                #endregion
                            }
                        }
                    }

                    if (c.Methods.Count == 0)
                    {
                        // skip class generation
                        continue;
                    }

                    results.Add(c);
                }
            }

            return results;
        }

        private void ReportDiagnostic(DiagnosticDescriptor desc, Location? location, params object?[]? messageArgs)
        {
            _reportDiagnostic(Diagnostic.Create(desc, location, messageArgs));
        }

        #region Type Symbol Helpers

        private static string ToDisplayString(ISymbol? symbol) => symbol?.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
                    SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
                )
            )
         ?? "";

        /// <summary>
        /// Determines if the specified type symbol is a nullable type.
        /// </summary>
        /// <param name="typeSymbol"></param>
        /// <returns>True if the type is a nullable type, otherwise false.</returns>
        private static bool IsNullableOfType(ITypeSymbol? typeSymbol)
        {
            if (typeSymbol is INamedTypeSymbol namedTypeSymbol
             && namedTypeSymbol.IsGenericType
             && namedTypeSymbol.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
            )
            {
                namedTypeSymbol.TypeArguments.FirstOrDefault();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Determines whether the type symbol is an array of an element type
        /// supported by Npgsql.
        /// </summary>
        /// <remarks>
        ///     <list type="bullet">
        ///         <item>The type symbol must be an IArrayTypeSymbol.</item>
        ///         <item>The type symbol's element type type symbol must be supported by Npgsql.</item>
        ///     </list>
        /// </remarks>
        /// <param name="typeSymbol"></param>
        /// <param name="elementTypeSymbol"></param>
        /// <returns>Returns true if the array's element type is supported by Npgsql, otherwise false.</returns>
        private bool IsArrayOfNpgsqlSupportedType(ITypeSymbol? typeSymbol, out ITypeSymbol? elementTypeSymbol)
        {
            elementTypeSymbol = null;

            if (typeSymbol is not IArrayTypeSymbol arrayTypeSymbol)
            {
                return false;
            }

            elementTypeSymbol = arrayTypeSymbol.ElementType;

            return IsSingleNpgsqlSupportedType(elementTypeSymbol);
        }

        /// <summary>
        /// Determines whether the type symbol is an array of an element type
        /// supported by Npgsql.
        /// </summary>
        /// <remarks>
        ///     <list type="bullet">
        ///         <item>The type symbol must be an INamedTypeSymbol.</item>
        ///         <item>The type symbol's original definition (the open generic type definition) must match List{T}.</item>
        ///         <item>The type symbol's only type argument type symbol must be supported by Npgsql.</item>
        ///     </list>
        /// </remarks>
        /// <param name="typeSymbol"></param>
        /// <param name="typeArgumentSymbol"></param>
        /// <returns>Returns true if the array's element type is supported by Npgsql, otherwise false.</returns>
        private bool IsListOfNpgsqlSupportedType(ITypeSymbol? typeSymbol, out ITypeSymbol? typeArgumentSymbol)
        {
            typeArgumentSymbol = null;

            if (typeSymbol is not INamedTypeSymbol namedTypeSymbol)
            {
                return false;
            }

            if (!SymbolEqualityComparer.Default.Equals(
                _knownTypeSymbols.ListOfTypeType,
                namedTypeSymbol.OriginalDefinition
            ))
            {
                return false;
            }

            typeArgumentSymbol = namedTypeSymbol.TypeArguments.FirstOrDefault();

            return IsSingleNpgsqlSupportedType(typeArgumentSymbol);
        }

        /// <summary>
        /// Determines whether the type symbol is a type supported by Npgsql,
        /// excluding arrays, collections, and any other custom constructable
        /// types (use IsArrayOfNpgsqlSupportedType and
        /// IsListOfNpgsqlSupportedType).
        /// </summary>
        /// <list type="bullet">
        ///     <item>If the type is a nullable reference type, T?, the not-annotated type symbol is extracted.</item>
        ///     <item>If the type is a nullable value type, Nullable{T}, the type argument is extracted.</item>
        ///     <item>The effective type symbol must be supported by Npgsql.</item>
        /// </list>
        /// <param name="typeSymbol"></param>
        /// <returns>Returns true if the type is supported by Npgsql, otherwise false.</returns>
        private bool IsSingleNpgsqlSupportedType(ITypeSymbol? typeSymbol)
        {
            if (typeSymbol is null)
            {
                return false;
            }

            if (typeSymbol.NullableAnnotation == NullableAnnotation.Annotated)
            {
                typeSymbol = typeSymbol.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
            }

            if (typeSymbol is INamedTypeSymbol namedTypeSymbol
             && namedTypeSymbol.IsGenericType
             && namedTypeSymbol.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                typeSymbol = namedTypeSymbol.TypeArguments.First();
            }

            return _npgsqlTypeSymbols.SupportedTypes.Contains(typeSymbol);
        }

        /// <summary>
        /// Determines whether the type symbol is a ValueTuple.
        /// </summary>
        /// <remarks>
        /// The type symbol for both Tuple and ValueTuple return true for
        /// IsTupleType. Tuple reports its complete closed generic type
        /// signature and is constructable, so we can cover it with
        /// IsConstructableType.
        /// <code>
        /// System.Tuple{long, string, DateTimeOffset}
        /// </code>
        /// However, the type symbol for ValueTuple erases the type name from
        /// the closed generic type signature, showing only the parenthetical
        /// construction, whether or not System.ValueTuple{T1} is written
        /// explicitly.
        /// <code>
        /// (long, string, DateTimeOffset)
        /// </code>
        /// and
        /// <code>
        /// (long Id, string Name, DateTimeOffset Created)
        /// </code>
        /// So, we use this method that compares name strings.
        /// <list type="bullet">
        ///     <item>The type symbol must be an INamedTypeSymbol.</item>
        ///     <item>The type symbol must return true for IsTupleType.</item>
        ///     <item>The type symbol's name must match 'System.ValueTuple' exactly.</item>
        /// </list>
        /// </remarks>
        /// <param name="typeSymbol"></param>
        /// <param name="tupleElements">The elements of the tuple, if any</param>
        /// <returns>Returns true if the type symbol is a ValueTuple, otherwise false.</returns>
        private bool IsValueTuple(ITypeSymbol? typeSymbol, out ImmutableArray<IFieldSymbol> tupleElements)
        {
            // TODO: rewrite to *exclude* Tuple, instead of trying to *include* ValueTuple
            if (typeSymbol is INamedTypeSymbol namedTypeSymbol
             && namedTypeSymbol.IsTupleType
             && string.Equals(_knownTypeSymbols.ValueTupleType?.Name, namedTypeSymbol.Name))
            {
                tupleElements = namedTypeSymbol.TupleElements;

                return true;
            }

            return false;
        }

        /// <summary>
        /// Determines whether the type symbol is a constructable type.
        /// </summary>
        /// <remarks>
        /// Matches most custom named types, even Tuple, but not ValueTuple
        /// (see the remarks on the IsValueTuple method for reasons).
        /// <list type="bullet">
        ///     <item>The type symbol must be an INamedTypeSymbol.</item>
        ///     <item>The type symbol must have at least one public non-abstract instance constructor.</item>
        ///     <item>The constructor with the most parameters will be selected.</item>
        /// </list>
        /// </remarks>
        /// <param name="typeSymbol"></param>
        /// <param name="constructorParameterSymbols">The parameters of the selected constructor, if any.</param>
        /// <returns>True if the type symbol is a constructable type, otherwise false.</returns>
        private static bool IsConstructableType(
            ITypeSymbol? typeSymbol,
            out ImmutableArray<IParameterSymbol> constructorParameterSymbols
        )
        {
            if (typeSymbol is INamedTypeSymbol namedTypeSymbol)
            {
                if (namedTypeSymbol.SpecialType != SpecialType.None)
                {
                    return false;
                }

                IMethodSymbol? constructorSymbol = namedTypeSymbol.InstanceConstructors
                    .Where(c => c.DeclaredAccessibility == Accessibility.Public && !c.IsAbstract)
                    .OrderByDescending(c => c.Parameters.Length)
                    .FirstOrDefault();

                if (constructorSymbol is not null)
                {
                    constructorParameterSymbols = constructorSymbol.Parameters;

                    return true;
                }
            }

            return false;
        }

        #endregion
    }
}