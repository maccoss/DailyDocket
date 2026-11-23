using Dapper;
using Microsoft.Data.Sqlite;

namespace DailyDocket.Data;

/// <summary>
/// The local database: a cache of Gmail, Calendar and Slack, encrypted with SQLCipher.
/// </summary>
/// <remarks>
/// It can be deleted and rebuilt by a full pull, so a schema change that cannot be migrated simply
/// starts over (see <see cref="Migrations"/>). A connection is opened per operation; SQLite pools
/// them, and WAL mode lets the UI read while a pull writes.
/// </remarks>
public sealed class DocketDatabase
{
    private static int s_initialized;
    private readonly string _connectionString;

    public DocketDatabase(string path, string key)
    {
        if (Interlocked.Exchange(ref s_initialized, 1) == 0)
        {
            SQLitePCL.Batteries_V2.Init();
        }

        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            // SQLCipher: Microsoft.Data.Sqlite issues PRAGMA key with this on open.
            Password = key,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    public string Path { get; }

    /// <summary>Where an unreadable database was moved on opening, or null when it opened normally.</summary>
    public string? MovedAside { get; private set; }

    /// <summary>Opens the database, creating or migrating it as needed.</summary>
    public static DocketDatabase Open(string path, string key)
    {
        var db = new DocketDatabase(path, key);
        try
        {
            using var c = db.Connect();
            Migrations.Apply(c);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 26)
        {
            // SQLITE_NOTADB: the file is not readable with this key (a lost Credential Manager
            // entry, a database from before encryption). The cache refills from a pull, but the
            // outbox does not: the file is moved aside, never deleted, and the outbox comes back
            // from outbox.json (OutboxStore).
            SqliteConnection.ClearAllPools();
            var aside = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, aside);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(path + suffix))
                {
                    File.Move(path + suffix, aside + suffix);
                }
            }

            db.MovedAside = aside;
            using var c = db.Connect();
            Migrations.Apply(c);
        }

        return db;
    }

    public SqliteConnection Connect()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        c.Execute("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return c;
    }

    /// <summary>The SQLCipher version, or null when the database is plain SQLite (which must never happen).</summary>
    public string? CipherVersion()
    {
        using var c = Connect();
        return c.ExecuteScalar<string?>("PRAGMA cipher_version;");
    }

    /// <summary>Dates are stored as UTC ISO 8601 text, which sorts and compares correctly in SQL.</summary>
    internal static string? Iso(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    internal static DateTimeOffset? FromIso(string? value) =>
        value is null ? null : DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
}
