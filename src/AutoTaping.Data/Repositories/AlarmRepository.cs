// 职责：报警仓储的 SQLite + Dapper 实现（写入、确认、关闭、查询、计数、CSV 导出）
// 层：Data/Repositories
// 依赖：SqliteConnectionFactory、CsvWriter、IAlarmRepository、Dapper

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Data.Repositories;

/// <summary>
/// 报警仓储实现。表结构见 <c>AlarmRecord</c>；等级数值与 <see cref="AlarmLevel"/> 一一对应。
/// 去重、自动恢复等业务规则由 S7 的 <c>AlarmService</c> 负责，本类只做持久化。
/// </summary>
public sealed class AlarmRepository : IAlarmRepository
{
    private const string SelectColumns = """
        Id, Code, Message, Level, Source, StartTime, AckTime, AckUser, EndTime, IsAcked
        """;

    private static readonly string[] CsvHeader =
    [
        "代码", "内容", "等级", "来源", "开始时间", "确认时间", "确认人", "恢复时间"
    ];

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly ILogger<AlarmRepository> _log;

    /// <summary>构造报警仓储。</summary>
    /// <param name="connectionFactory">连接工厂。</param>
    /// <param name="clock">时钟。</param>
    /// <param name="log">日志。</param>
    public AlarmRepository(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        ILogger<AlarmRepository> log)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _log = log;
    }

    /// <inheritdoc />
    public void Insert(AlarmRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        var startTime = record.StartTime == default ? _clock.Now : record.StartTime;

        connection.Execute(
            """
            INSERT INTO AlarmRecord (Code, Message, Level, Source, StartTime, AckTime, AckUser, EndTime, IsAcked)
            VALUES (@Code, @Message, @Level, @Source, @StartTime, @AckTime, @AckUser, @EndTime, @IsAcked);
            """,
            new
            {
                record.Code,
                record.Message,
                Level = (int)record.Level,
                record.Source,
                StartTime = SqliteTime.ToDb(startTime),
                AckTime = SqliteTime.ToDb(record.AckTime),
                record.AckUser,
                EndTime = SqliteTime.ToDb(record.EndTime),
                IsAcked = record.IsAcked ? 1 : 0
            },
            transaction);

        transaction.Commit();

        _log.LogDebug("写入报警 {Code}（{Level}）{Message}", record.Code, record.Level, record.Message);
    }

    /// <inheritdoc />
    public void Ack(int id, string user)
    {
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        var affected = connection.Execute(
            """
            UPDATE AlarmRecord
            SET IsAcked = 1, AckTime = @AckTime, AckUser = @AckUser
            WHERE Id = @Id AND IsAcked = 0;
            """,
            new { Id = id, AckTime = SqliteTime.ToDb(_clock.Now), AckUser = user },
            transaction);

        transaction.Commit();

        if (affected == 0)
        {
            // 已确认过或 Id 不存在：报错了才能让界面刷新列表，避免"点了没反应"
            throw new KeyNotFoundException($"报警不存在或已确认（Id={id}）");
        }

        _log.LogInformation("确认报警 {AlarmId}，确认人 {User}", id, user);
    }

    /// <inheritdoc />
    public void CloseAllActive()
    {
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        var affected = connection.Execute(
            "UPDATE AlarmRecord SET EndTime = @EndTime WHERE EndTime IS NULL;",
            new { EndTime = SqliteTime.ToDb(_clock.Now) },
            transaction);

        transaction.Commit();

        _log.LogInformation("关闭全部生效中报警，共 {Count} 条", affected);
    }

    /// <inheritdoc />
    public void Close(int id, DateTime endTime)
    {
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        var affected = connection.Execute(
            """
            UPDATE AlarmRecord
            SET EndTime = @EndTime
            WHERE Id = @Id AND EndTime IS NULL;
            """,
            new { Id = id, EndTime = SqliteTime.ToDb(endTime) },
            transaction);

        transaction.Commit();

        if (affected == 0)
        {
            // 已被关闭（并发恢复 / 复位时已 CloseAllActive）：恢复是幂等动作，只记日志不抛异常
            _log.LogDebug("报警 {AlarmId} 已恢复或不存在，跳过", id);
            return;
        }

        _log.LogInformation("报警 {AlarmId} 已写恢复时间（{EndTime:HH:mm:ss}）", id, endTime);
    }

    /// <inheritdoc />
    public IReadOnlyList<AlarmRecord> QueryActive()
    {
        using var connection = _connectionFactory.Create();

        var rows = connection.Query<AlarmRow>(
            $"""
            SELECT {SelectColumns} FROM AlarmRecord
            WHERE IsAcked = 0 AND EndTime IS NULL
            ORDER BY StartTime DESC, Id DESC;
            """);

        return rows.Select(Map).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<AlarmRecord> Query(DateTime from, DateTime to, bool onlyUnacked = false)
    {
        using var connection = _connectionFactory.Create();

        var sql = $"""
            SELECT {SelectColumns} FROM AlarmRecord
            WHERE StartTime >= @From AND StartTime <= @To
            """;
        if (onlyUnacked)
        {
            sql += " AND IsAcked = 0";
        }

        sql += " ORDER BY StartTime DESC, Id DESC;";

        var rows = connection.Query<AlarmRow>(sql, new
        {
            From = SqliteTime.ToDb(from),
            To = SqliteTime.ToDb(to)
        });

        return rows.Select(Map).ToList();
    }

    /// <inheritdoc />
    public int CountUnacked(int minLevel)
    {
        using var connection = _connectionFactory.Create();

        return (int)connection.ExecuteScalar<long>(
            """
            SELECT COUNT(1) FROM AlarmRecord
            WHERE IsAcked = 0 AND Level >= @MinLevel AND EndTime IS NULL;
            """,
            new { MinLevel = minLevel });
    }

    /// <inheritdoc />
    public void ExportCsv(DateTime from, DateTime to, string path)
    {
        var records = Query(from, to);

        var rows = records.Select(record => (IReadOnlyList<string>)
        [
            record.Code,
            record.Message,
            GetLevelText(record.Level),
            record.Source,
            record.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
            record.AckTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
            record.AckUser ?? "",
            record.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""
        ]).ToList();

        CsvWriter.Write(path, CsvHeader, rows);
        _log.LogInformation("导出报警 CSV {Path}，共 {Count} 条", path, rows.Count);
    }

    private static string GetLevelText(AlarmLevel level) => level switch
    {
        AlarmLevel.Info => "提示",
        AlarmLevel.Warning => "警告",
        AlarmLevel.Critical => "严重",
        _ => level.ToString()
    };

    private static AlarmRecord Map(AlarmRow row) => new()
    {
        Id = row.Id,
        Code = row.Code,
        Message = row.Message,
        Level = (AlarmLevel)row.Level,
        Source = row.Source,
        StartTime = SqliteTime.FromDb(row.StartTime),
        AckTime = row.AckTime is null ? null : SqliteTime.FromDb(row.AckTime),
        AckUser = row.AckUser,
        EndTime = row.EndTime is null ? null : SqliteTime.FromDb(row.EndTime),
        IsAcked = row.IsAcked != 0
    };

    /// <summary>AlarmRecord 表的一行。</summary>
    private sealed class AlarmRow
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
        public long Level { get; set; }
        public string Source { get; set; } = "";
        public string StartTime { get; set; } = "";
        public string? AckTime { get; set; }
        public string? AckUser { get; set; }
        public string? EndTime { get; set; }
        public long IsAcked { get; set; }
    }
}
