// 职责：系统状态键值仓储的 SQLite + Dapper 实现（断电检测依赖它读写 LastShutdownClean）
// 层：Data/Repositories
// 依赖：SqliteConnectionFactory、ISystemStateRepository、Dapper

using AutoTaping.Core.Abstractions;
using Dapper;

namespace AutoTaping.Data.Repositories;

/// <summary>
/// 系统状态仓储实现。按主键 <c>Key</c> 做 upsert（SQLite 3.24+ 的 <c>ON CONFLICT</c>），
/// 保证同一键只有一行、且写入是原子的。
/// </summary>
public sealed class SystemStateRepository : ISystemStateRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;

    /// <summary>构造系统状态仓储。</summary>
    /// <param name="connectionFactory">连接工厂。</param>
    /// <param name="clock">时钟（写 UpdatedAt）。</param>
    public SystemStateRepository(SqliteConnectionFactory connectionFactory, IClock clock)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
    }

    /// <inheritdoc />
    public string? Get(string key)
    {
        using var connection = _connectionFactory.Create();
        return connection.ExecuteScalar<string?>(
            "SELECT Value FROM SystemState WHERE Key = @Key;",
            new { Key = key });
    }

    /// <inheritdoc />
    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        connection.Execute(
            """
            INSERT INTO SystemState (Key, Value, UpdatedAt)
            VALUES (@Key, @Value, @UpdatedAt)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value, UpdatedAt = excluded.UpdatedAt;
            """,
            new { Key = key, Value = value, UpdatedAt = SqliteTime.ToDb(_clock.Now) },
            transaction);

        transaction.Commit();
    }
}
