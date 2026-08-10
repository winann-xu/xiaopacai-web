using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace XiaopacaiWeb.Data;

/// <summary>
/// EF Core 连接拦截器 — 在每次连接打开时执行 PRAGMA key 启用 SQLCipher 加密
/// </summary>
public class SqlCipherInterceptor : DbConnectionInterceptor
{
    private readonly string _dbPassword;

    public SqlCipherInterceptor(string dbPassword)
    {
        _dbPassword = dbPassword;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        SetPragmaKey(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection,
        ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await SetPragmaKeyAsync(connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private void SetPragmaKey(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA key = '{EscapeSqlString(_dbPassword)}';";
        cmd.ExecuteNonQuery();
    }

    private async Task SetPragmaKeyAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA key = '{EscapeSqlString(_dbPassword)}';";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string EscapeSqlString(string s) =>
        s.Replace("'", "''");
}
