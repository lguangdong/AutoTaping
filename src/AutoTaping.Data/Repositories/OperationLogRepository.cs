// 职责：操作日志仓储的 SQLite + Dapper 实现（写入与区间查询）
// 层：Data/Repositories
// 依赖：SqliteConnectionFactory、IOperationLogRepository、Dapper

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Data.Repositories;

/// <summary>
/// 操作日志仓储实现。表结构见 <c>OperationLog</c>；写入时间由仓储统一取时钟，避免调用方漏填。
/// </summary>
public sealed class OperationLogRepository : IOperationLogRepository
{
    private const string SelectColumns = "Id, UserName, Action, Target, OldValue, NewValue, CreatedAt";

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly ILogger<OperationLogRepository> _log;

    /// <summary>构造操作日志仓储。</summary>
    /// <param name="connectionFactory">连接工厂。</param>
    /// <param name="clock">时钟。</param>
    /// <param name="log">日志。</param>
    public OperationLogRepository(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        ILogger<OperationLogRepository> log)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _log = log;
    }

    /// <inheritdoc />
    public void Insert(string user, string action, string target, string? oldValue, string? newValue)
    {
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        connection.Execute(
            """
            INSERT INTO OperationLog (UserName, Action, Target, OldValue, NewValue, CreatedAt)
            VALUES (@UserName, @Action, @Target, @OldValue, @NewValue, @CreatedAt);
            """,
            new
            {
                UserName = user,
                Action = action,
                Target = target,
                OldValue = oldValue,
                NewValue = newValue,
                CreatedAt = SqliteTime.ToDb(_clock.Now)
            },
            transaction);

        transaction.Commit();

        _log.LogInformation("操作日志：{User} {Action} {Target} {OldValue} -> {NewValue}",
                            user, action, target, oldValue, newValue);
    }

    /// <inheritdoc />
    public IReadOnlyList<OperationLog> Query(DateTime from, DateTime to)
    {
        using var connection = _connectionFactory.Create();

        var rows = connection.Query<OperationLogRow>(
            $"""
            SELECT {SelectColumns} FROM OperationLog
            WHERE CreatedAt >= @From AND CreatedAt <= @To
            ORDER BY CreatedAt DESC, Id DESC;
            """,
            new { From = SqliteTime.ToDb(from), To = SqliteTime.ToDb(to) });

        return rows.Select(row => new OperationLog
        {
            Id = row.Id,
            UserName = row.UserName,
            Action = row.Action,
            Target = row.Target,
            OldValue = row.OldValue,
            NewValue = row.NewValue,
            CreatedAt = SqliteTime.FromDb(row.CreatedAt)
        }).ToList();
    }

    /// <summary>OperationLog 表的一行。</summary>
    private sealed class OperationLogRow
    {
        public int Id { get; set; }
        public string UserName { get; set; } = "";
        public string Action { get; set; } = "";
        public string Target { get; set; } = "";
        public string? OldValue { get; set; }
        public string? NewValue { get; set; }
        public string CreatedAt { get; set; } = "";
    }
}
