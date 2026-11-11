// 职责：停机记录仓储的 SQLite + Dapper 实现（写入与区间查询）
// 层：Data/Repositories
// 依赖：SqliteConnectionFactory、IDowntimeRepository、Dapper

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Data.Repositories;

/// <summary>
/// 停机记录仓储实现。表结构见 <c>SqliteSchema</c> 中的 <c>DowntimeRecord</c>，
/// 分类口径见 <c>docs/05-核心状态机与流程.md</c> 第 7.2 节。
/// </summary>
public sealed class DowntimeRepository : IDowntimeRepository
{
    private const string SelectColumns = "Id, Reason, StartTime, EndTime, DurationSeconds";

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly ILogger<DowntimeRepository> _log;

    /// <summary>构造停机记录仓储。</summary>
    /// <param name="connectionFactory">连接工厂。</param>
    /// <param name="clock">时钟（开始时间缺省时兜底）。</param>
    /// <param name="log">日志。</param>
    public DowntimeRepository(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        ILogger<DowntimeRepository> log)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _log = log;
    }

    /// <inheritdoc />
    public int Insert(DowntimeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        var startTime = record.StartTime == default ? _clock.Now : record.StartTime;

        connection.Execute(
            """
            INSERT INTO DowntimeRecord (Reason, StartTime, EndTime, DurationSeconds)
            VALUES (@Reason, @StartTime, @EndTime, @DurationSeconds);
            """,
            new
            {
                record.Reason,
                StartTime = SqliteTime.ToDb(startTime),
                EndTime = SqliteTime.ToDb(record.EndTime),
                record.DurationSeconds
            },
            transaction);

        var newId = (int)connection.ExecuteScalar<long>("SELECT last_insert_rowid();", transaction: transaction);
        transaction.Commit();

        record.Id = newId;
        _log.LogDebug("写入停机记录 {Id}：{Reason}，时长 {Seconds}s", newId, record.Reason, record.DurationSeconds);
        return newId;
    }

    /// <inheritdoc />
    public IReadOnlyList<DowntimeRecord> Query(DateTime from, DateTime to)
    {
        using var connection = _connectionFactory.Create();

        var rows = connection.Query<DowntimeRow>(
            $"""
            SELECT {SelectColumns} FROM DowntimeRecord
            WHERE StartTime >= @From AND StartTime <= @To
            ORDER BY StartTime DESC, Id DESC;
            """,
            new { From = SqliteTime.ToDb(from), To = SqliteTime.ToDb(to) });

        return rows.Select(row => new DowntimeRecord
        {
            Id = row.Id,
            Reason = row.Reason,
            StartTime = SqliteTime.FromDb(row.StartTime),
            EndTime = row.EndTime is null ? null : SqliteTime.FromDb(row.EndTime),
            DurationSeconds = row.DurationSeconds
        }).ToList();
    }

    /// <summary>DowntimeRecord 表的一行。</summary>
    private sealed class DowntimeRow
    {
        public int Id { get; set; }
        public string Reason { get; set; } = "";
        public string StartTime { get; set; } = "";
        public string? EndTime { get; set; }
        public int DurationSeconds { get; set; }
    }
}
