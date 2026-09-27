using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace NightSignal.ControlPlane.Persistence;

/// <summary>
/// SQLite store for local development and tests. Writers use BEGIN IMMEDIATE, which takes the database write
/// lock up front, so read-check-write sequences inside a transaction cannot interleave (across processes too).
/// </summary>
public sealed class SqliteGameStore : SqlGameStore
{
    readonly string connectionString;

    public SqliteGameStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    protected override string Dialect => "sqlite";
    protected override string ForUpdate => "";
    protected override string MigrationsTableDdl =>
        "CREATE TABLE IF NOT EXISTS schema_migrations (version TEXT PRIMARY KEY, applied_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP)";

    public override async Task InitializeAsync(CancellationToken ct = default)
    {
        await using (DbConnection c = await OpenAsync(ct))
            await c.ExecAsync(null, "PRAGMA journal_mode = WAL"); // readers never block the single writer
        await base.InitializeAsync(ct);
    }

    protected override async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var c = new SqliteConnection(connectionString);
        await c.OpenAsync(ct);
        await c.ExecAsync(null, "PRAGMA busy_timeout = 15000");
        return c;
    }

    protected override Task<DbTransaction> BeginWriteAsync(DbConnection c, CancellationToken ct) =>
        Task.FromResult<DbTransaction>(((SqliteConnection)c).BeginTransaction(deferred: false)); // BEGIN IMMEDIATE

    protected override Task<DbTransaction> BeginReadAsync(DbConnection c, CancellationToken ct) =>
        Task.FromResult<DbTransaction>(((SqliteConnection)c).BeginTransaction(deferred: true)); // snapshot read

    protected override bool IsUniqueViolation(Exception e) =>
        e is SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 2067 or 1555 };
}

/// <summary>
/// PostgreSQL (Supabase) store. Shares every statement with SQLite; row locks come from SELECT ... FOR UPDATE
/// under READ COMMITTED, and lost UNIQUE races are retried by <see cref="SqlGameStore"/>.
/// NOT EXECUTED in this environment (no PostgreSQL server was available); it compiles and is kept in step.
/// </summary>
public sealed class PostgresGameStore(string connectionString) : SqlGameStore, IAsyncDisposable
{
    readonly NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);

    protected override string Dialect => "postgres";
    protected override string ForUpdate => " FOR UPDATE";
    protected override string MigrationsTableDdl =>
        "CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP)";

    protected override async Task<DbConnection> OpenAsync(CancellationToken ct) => await dataSource.OpenConnectionAsync(ct);

    protected override async Task<DbTransaction> BeginWriteAsync(DbConnection c, CancellationToken ct) =>
        await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

    protected override async Task<DbTransaction> BeginReadAsync(DbConnection c, CancellationToken ct) =>
        await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

    protected override bool IsUniqueViolation(Exception e) => e is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();
}
