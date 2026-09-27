using System.Data.Common;
using System.Reflection;

namespace NightSignal.ControlPlane.Persistence;

/// <summary>Minimal ADO.NET helpers so SQLite and PostgreSQL share the same SQL text.</summary>
internal static class Db
{
    public static DbCommand Command(this DbConnection c, DbTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        DbCommand cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach ((string name, object? value) in args)
        {
            DbParameter p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    public static async Task<int> ExecAsync(this DbConnection c, DbTransaction? tx, string sql, params (string, object?)[] args)
    {
        await using DbCommand cmd = c.Command(tx, sql, args);
        return await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<List<T>> QueryAsync<T>(this DbConnection c, DbTransaction? tx, string sql,
        Func<DbDataReader, T> map, params (string, object?)[] args)
    {
        await using DbCommand cmd = c.Command(tx, sql, args);
        await using DbDataReader r = await cmd.ExecuteReaderAsync();
        var list = new List<T>();
        while (await r.ReadAsync())
            list.Add(map(r));
        return list;
    }

    public static async Task<T?> FirstOrDefaultAsync<T>(this DbConnection c, DbTransaction? tx, string sql,
        Func<DbDataReader, T> map, params (string, object?)[] args)
    {
        List<T> rows = await c.QueryAsync(tx, sql, map, args);
        return rows.Count > 0 ? rows[0] : default;
    }

    // Dialect-neutral readers (SQLite INTEGER is always Int64; PostgreSQL integer is Int32).
    public static long Long(this DbDataReader r, int i) => Convert.ToInt64(r.GetValue(i));
    public static int Int(this DbDataReader r, int i) => Convert.ToInt32(r.GetValue(i));
    public static string Str(this DbDataReader r, int i) => r.GetString(i);
    public static string? NStr(this DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary>Ordered embedded migration scripts for a dialect ("sqlite" or "postgres").</summary>
    public static IEnumerable<(string Version, string Sql)> Migrations(string dialect)
    {
        Assembly asm = typeof(Db).Assembly;
        string prefix = $"migrations.{dialect}.";
        foreach (string name in asm.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using Stream s = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(s);
            yield return (name[prefix.Length..^".sql".Length], reader.ReadToEnd());
        }
    }
}
