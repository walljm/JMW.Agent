// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
    "Security",
    "CA2100:Review SQL queries for security vulnerabilities",
    Justification = "For development purposes only",
    Scope = "member",
    Target =
        "~M:ITPIE.Database.Abstractions.Testing.DatabaseCommandValidatorExtensions.ValidateAsync(Npgsql.NpgsqlConnection,System.String,System.Collections.Generic.IReadOnlyList{Npgsql.NpgsqlParameter},System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Collections.ObjectModel.ReadOnlyCollection{System.Data.Common.DbColumn}}"
)]