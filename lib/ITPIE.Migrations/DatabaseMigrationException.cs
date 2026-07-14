namespace ITPIE.Migrations;

public sealed class DatabaseMigrationException : Exception
{
    public DatabaseMigrationException() { }

    public DatabaseMigrationException(string? message)
        : base(message) { }

    public DatabaseMigrationException(string? message, Exception? innerException)
        : base(message, innerException) { }
}