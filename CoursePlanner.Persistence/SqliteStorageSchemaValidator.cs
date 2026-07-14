using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace CoursePlanner.Persistence;

internal static class SqliteStorageSchemaValidator
{
    private const string AppStateCreateSql = """
        CREATE TABLE app_state (
            id TEXT PRIMARY KEY NOT NULL,
            json TEXT NOT NULL,
            updated_at TEXT NOT NULL
        )
        """;

    private const string AppEventsCreateSql = """
        CREATE TABLE app_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            occurred_at TEXT NOT NULL,
            level TEXT NOT NULL,
            event_name TEXT NOT NULL,
            message TEXT NOT NULL
        )
        """;

    private static readonly string CreateMissingTablesSql =
        AppStateCreateSql.Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal) +
        ";\n" +
        AppEventsCreateSql.Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal) +
        ";";

    private static readonly StorageColumn[] AppStateColumns =
    [
        new("id", "TEXT", IsNotNull: true, PrimaryKeyOrdinal: 1),
        new("json", "TEXT", IsNotNull: true, PrimaryKeyOrdinal: 0),
        new("updated_at", "TEXT", IsNotNull: true, PrimaryKeyOrdinal: 0)
    ];

    private static readonly StorageColumn[] AppEventColumns =
    [
        new("id", "INTEGER", IsNotNull: false, PrimaryKeyOrdinal: 1),
        new("occurred_at", "TEXT", IsNotNull: true, PrimaryKeyOrdinal: 0),
        new("level", "TEXT", IsNotNull: true, PrimaryKeyOrdinal: 0),
        new("event_name", "TEXT", IsNotNull: true, PrimaryKeyOrdinal: 0),
        new("message", "TEXT", IsNotNull: true, PrimaryKeyOrdinal: 0)
    ];

    public static void Validate(SqliteConnection connection, bool requireDefaultState)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ValidateUserObjects(connection);
        ValidateCreateSql(connection, "app_state", AppStateCreateSql);
        ValidateCreateSql(connection, "app_events", AppEventsCreateSql);
        ValidateTable(connection, "app_state", AppStateColumns);
        ValidateTable(connection, "app_events", AppEventColumns);
        ValidateStateRows(connection, requireDefaultState);
    }

    public static void EnsureCreated(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = CreateMissingTablesSql;
        command.ExecuteNonQuery();
    }

    private static void ValidateUserObjects(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, name
            FROM sqlite_master
            WHERE name NOT LIKE 'sqlite\_%' ESCAPE '\'
            ORDER BY type, name
            """;
        using var reader = command.ExecuteReader();
        var objects = new List<(string Type, string Name)>();
        while (reader.Read())
            objects.Add((reader.GetString(0), reader.GetString(1)));

        if (!objects.SequenceEqual(
                new[]
                {
                    (Type: "table", Name: "app_events"),
                    (Type: "table", Name: "app_state")
                }))
        {
            throw InvalidSchema();
        }
    }

    private static void ValidateTable(
        SqliteConnection connection,
        string tableName,
        IReadOnlyList<StorageColumn> expectedColumns)
    {
        using var command = connection.CreateCommand();
        command.CommandText = tableName switch
        {
            "app_state" => "PRAGMA table_info('app_state')",
            "app_events" => "PRAGMA table_info('app_events')",
            _ => throw new ArgumentOutOfRangeException(nameof(tableName), tableName, null)
        };
        using var reader = command.ExecuteReader();
        var index = 0;
        while (reader.Read())
        {
            if (index >= expectedColumns.Count)
                throw InvalidSchema();

            var expected = expectedColumns[index];
            if (reader.GetInt32(0) != index ||
                !string.Equals(reader.GetString(1), expected.Name, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(2), expected.Type, StringComparison.OrdinalIgnoreCase) ||
                reader.GetInt32(3) != (expected.IsNotNull ? 1 : 0) ||
                !reader.IsDBNull(4) ||
                reader.GetInt32(5) != expected.PrimaryKeyOrdinal)
            {
                throw InvalidSchema();
            }
            index++;
        }

        if (index != expectedColumns.Count)
            throw InvalidSchema();
    }

    private static void ValidateCreateSql(
        SqliteConnection connection,
        string tableName,
        string expectedSql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", tableName);
        var actualSql = command.ExecuteScalar() as string;
        if (actualSql is null || !string.Equals(
                NormalizeSql(actualSql),
                NormalizeSql(expectedSql),
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidSchema();
        }
    }

    private static string NormalizeSql(string sql) =>
        Regex.Replace(sql, @"\s+", " ").Trim().TrimEnd(';');

    private static void ValidateStateRows(SqliteConnection connection, bool requireDefaultState)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
                   SUM(CASE WHEN id = 'default' THEN 1 ELSE 0 END)
            FROM app_state
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw InvalidSchema();

        var rowCount = reader.GetInt64(0);
        var defaultCount = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
        var valid = requireDefaultState
            ? rowCount == 1 && defaultCount == 1
            : rowCount == 0 || (rowCount == 1 && defaultCount == 1);
        if (!valid)
            throw InvalidSchema();
    }

    private static InvalidDataException InvalidSchema() =>
        new("The SQLite storage schema is not the exact current application schema.");

    private readonly record struct StorageColumn(
        string Name,
        string Type,
        bool IsNotNull,
        int PrimaryKeyOrdinal);
}
