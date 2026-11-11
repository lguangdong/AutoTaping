// 职责：班次产量汇总仓储的 SQLite + Dapper 实现（按 ShiftDate + ShiftName 幂等覆盖写入）
// 层：Data/Repositories
// 依赖：SqliteConnectionFactory、IShiftProductionRepository、Dapper

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Data.Repositories;

/// <summary>
/// 班次产量汇总仓储实现。表 <c>ShiftProduction</c> 上 <c>UNIQUE(ShiftDate, ShiftName)</c>，
/// 写入用 SQLite 的 UPSERT，保证「重算汇总」可以反复执行而不会产生重复行。
/// </summary>
public sealed class ShiftProductionRepository : IShiftProductionRepository
{
    private const string SelectColumns = """
        Id, ShiftDate, ShiftName, TotalCount, OkCount, NgCount, RunSeconds, DownSeconds, LastUpdateAt
        """;

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ILogger<ShiftProductionRepository> _log;

    /// <summary>构造班次汇总仓储。</summary>
    /// <param name="connectionFactory">连接工厂。</param>
    /// <param name="log">日志。</param>
    public ShiftProductionRepository(SqliteConnectionFactory connectionFactory, ILogger<ShiftProductionRepository> log)
    {
        _connectionFactory = connectionFactory;
        _log = log;
    }

    /// <inheritdoc />
    public void Upsert(ShiftProduction summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        connection.Execute(
            """
            INSERT INTO ShiftProduction
                (ShiftDate, ShiftName, TotalCount, OkCount, NgCount, RunSeconds, DownSeconds, LastUpdateAt)
            VALUES (@ShiftDate, @ShiftName, @TotalCount, @OkCount, @NgCount, @RunSeconds, @DownSeconds, @LastUpdateAt)
            ON CONFLICT(ShiftDate, ShiftName) DO UPDATE SET
                TotalCount   = excluded.TotalCount,
                OkCount      = excluded.OkCount,
                NgCount      = excluded.NgCount,
                RunSeconds   = excluded.RunSeconds,
                DownSeconds  = excluded.DownSeconds,
                LastUpdateAt = excluded.LastUpdateAt;
            """,
            new
            {
                ShiftDate = SqliteTime.ToDbDate(summary.ShiftDate),
                summary.ShiftName,
                summary.TotalCount,
                summary.OkCount,
                summary.NgCount,
                summary.RunSeconds,
                summary.DownSeconds,
                LastUpdateAt = SqliteTime.ToDb(summary.LastUpdateAt)
            },
            transaction);

        transaction.Commit();

        _log.LogDebug("班次汇总已写入：{Date} {Shift}，产出 {Total}", summary.ShiftDate.ToString("yyyy-MM-dd"),
                      summary.ShiftName, summary.TotalCount);
    }

    /// <inheritdoc />
    public IReadOnlyList<ShiftProduction> Query(DateTime date)
    {
        using var connection = _connectionFactory.Create();

        var rows = connection.Query<ShiftRow>(
            $"""
            SELECT {SelectColumns} FROM ShiftProduction
            WHERE ShiftDate = @ShiftDate
            ORDER BY ShiftName ASC;
            """,
            new { ShiftDate = SqliteTime.ToDbDate(date) });

        return rows.Select(row => new ShiftProduction
        {
            Id = row.Id,
            ShiftDate = SqliteTime.FromDbDate(row.ShiftDate),
            ShiftName = row.ShiftName,
            TotalCount = row.TotalCount,
            OkCount = row.OkCount,
            NgCount = row.NgCount,
            RunSeconds = row.RunSeconds,
            DownSeconds = row.DownSeconds,
            LastUpdateAt = SqliteTime.FromDb(row.LastUpdateAt)
        }).ToList();
    }

    /// <summary>ShiftProduction 表的一行。</summary>
    private sealed class ShiftRow
    {
        public int Id { get; set; }
        public string ShiftDate { get; set; } = "";
        public string ShiftName { get; set; } = "";
        public int TotalCount { get; set; }
        public int OkCount { get; set; }
        public int NgCount { get; set; }
        public int RunSeconds { get; set; }
        public int DownSeconds { get; set; }
        public string LastUpdateAt { get; set; } = "";
    }
}
