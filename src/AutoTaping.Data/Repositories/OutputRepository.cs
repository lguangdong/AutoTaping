// 职责：产出记录仓储的 SQLite + Dapper 实现（写入、当日计数、流水号、区间统计、CSV 导出）
// 层：Data/Repositories
// 依赖：SqliteConnectionFactory、CsvWriter、IOutputRepository、Dapper

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Data.Repositories;

/// <summary>
/// 产出记录仓储实现。表结构见 <c>OutputRecord</c>，统计口径见 06 文档第 6 节。
/// </summary>
public sealed class OutputRepository : IOutputRepository
{
    private const string SelectColumns = """
        Id, SequenceNo, RecipeId, RecipeName, Result, CycleTimeMs, DeltaX, DeltaY, DeltaTheta,
        MatchScore1, MatchScore2, NgReason, Shift, CreatedAt
        """;

    private static readonly string[] CsvHeader =
    [
        "序号", "时间", "配方", "结果", "节拍(ms)", "ΔX(mm)", "ΔY(mm)", "Δθ(°)", "NG原因", "班次"
    ];

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly ILogger<OutputRepository> _log;

    /// <summary>构造产出仓储。</summary>
    /// <param name="connectionFactory">连接工厂。</param>
    /// <param name="clock">时钟（用于"今日"边界）。</param>
    /// <param name="log">日志。</param>
    public OutputRepository(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        ILogger<OutputRepository> log)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _log = log;
    }

    /// <inheritdoc />
    public void Insert(OutputRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        var createdAt = record.CreatedAt == default ? _clock.Now : record.CreatedAt;

        connection.Execute(
            """
            INSERT INTO OutputRecord (SequenceNo, RecipeId, RecipeName, Result, CycleTimeMs,
                                      DeltaX, DeltaY, DeltaTheta, MatchScore1, MatchScore2,
                                      NgReason, Shift, CreatedAt)
            VALUES (@SequenceNo, @RecipeId, @RecipeName, @Result, @CycleTimeMs,
                    @DeltaX, @DeltaY, @DeltaTheta, @MatchScore1, @MatchScore2,
                    @NgReason, @Shift, @CreatedAt);
            """,
            new
            {
                record.SequenceNo,
                record.RecipeId,
                record.RecipeName,
                record.Result,
                record.CycleTimeMs,
                record.DeltaX,
                record.DeltaY,
                record.DeltaTheta,
                record.MatchScore1,
                record.MatchScore2,
                record.NgReason,
                record.Shift,
                CreatedAt = SqliteTime.ToDb(createdAt)
            },
            transaction);

        transaction.Commit();

        _log.LogDebug("写入产出记录 序号 {SequenceNo} 结果 {Result} 节拍 {CycleTimeMs}ms",
                      record.SequenceNo, record.Result, record.CycleTimeMs);
    }

    /// <inheritdoc />
    public int CountToday(string result)
    {
        var (start, end) = GetDayRange(_clock.Now);

        using var connection = _connectionFactory.Create();
        return (int)connection.ExecuteScalar<long>(
            """
            SELECT COUNT(1) FROM OutputRecord
            WHERE Result = @Result AND CreatedAt >= @Start AND CreatedAt < @End;
            """,
            new { Result = result, Start = SqliteTime.ToDb(start), End = SqliteTime.ToDb(end) });
    }

    /// <inheritdoc />
    public int NextSequenceNo(DateTime date)
    {
        var (start, end) = GetDayRange(date);

        using var connection = _connectionFactory.Create();
        var max = connection.ExecuteScalar<long>(
            """
            SELECT IFNULL(MAX(SequenceNo), 0) FROM OutputRecord
            WHERE CreatedAt >= @Start AND CreatedAt < @End;
            """,
            new { Start = SqliteTime.ToDb(start), End = SqliteTime.ToDb(end) });

        return (int)max + 1;
    }

    /// <inheritdoc />
    public OutputStatistics GetStatistics(DateTime from, DateTime to)
    {
        using var connection = _connectionFactory.Create();

        var row = connection.QuerySingle<StatisticsRow>(
            """
            SELECT COUNT(1)                                                             AS TotalCount,
                   IFNULL(SUM(CASE WHEN Result = @Ok THEN 1 ELSE 0 END), 0)              AS OkCount,
                   IFNULL(AVG(CycleTimeMs), 0)                                          AS AverageCycleTimeMs
            FROM OutputRecord
            WHERE CreatedAt >= @From AND CreatedAt <= @To;
            """,
            new
            {
                Ok = OutputRecord.ResultOk,
                From = SqliteTime.ToDb(from),
                To = SqliteTime.ToDb(to)
            });

        return new OutputStatistics
        {
            TotalCount = row.TotalCount,
            OkCount = row.OkCount,
            AverageCycleTimeMs = row.AverageCycleTimeMs
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<OutputRecord> Query(DateTime from, DateTime to, string? shift, int maxCount)
    {
        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), "返回条数必须大于 0");
        }

        using var connection = _connectionFactory.Create();

        var sql = $"""
            SELECT {SelectColumns} FROM OutputRecord
            WHERE CreatedAt >= @From AND CreatedAt <= @To
            """;
        if (!string.IsNullOrWhiteSpace(shift))
        {
            sql += " AND Shift = @Shift";
        }

        sql += " ORDER BY CreatedAt DESC, Id DESC LIMIT @MaxCount;";

        var rows = connection.Query<OutputRow>(sql, new
        {
            From = SqliteTime.ToDb(from),
            To = SqliteTime.ToDb(to),
            Shift = shift,
            MaxCount = maxCount
        });

        return rows.Select(MapRecord).ToList();
    }

    /// <inheritdoc />
    public void ExportCsv(DateTime from, DateTime to, string path)
    {
        using var connection = _connectionFactory.Create();

        var records = connection.Query<OutputRow>(
            $"""
            SELECT {SelectColumns} FROM OutputRecord
            WHERE CreatedAt >= @From AND CreatedAt <= @To
            ORDER BY SequenceNo ASC;
            """,
            new { From = SqliteTime.ToDb(from), To = SqliteTime.ToDb(to) }).ToList();

        var rows = records.Select(row => (IReadOnlyList<string>)
        [
            row.SequenceNo.ToString(),
            row.CreatedAt,
            row.RecipeName,
            row.Result,
            row.CycleTimeMs.ToString(),
            row.DeltaX.ToString("F2"),
            row.DeltaY.ToString("F2"),
            row.DeltaTheta.ToString("F2"),
            row.NgReason ?? "",
            row.Shift
        ]).ToList();

        CsvWriter.Write(path, CsvHeader, rows);
        _log.LogInformation("导出产出 CSV {Path}，共 {Count} 条", path, rows.Count);
    }

    /// <summary>取某一天的 [00:00:00, 次日 00:00:00) 区间。</summary>
    private static (DateTime Start, DateTime End) GetDayRange(DateTime date)
        => (date.Date, date.Date.AddDays(1));

    /// <summary>把查询行映射成产出记录实体。</summary>
    private static OutputRecord MapRecord(OutputRow row) => new()
    {
        Id = row.Id,
        SequenceNo = row.SequenceNo,
        RecipeId = row.RecipeId,
        RecipeName = row.RecipeName,
        Result = row.Result,
        CycleTimeMs = row.CycleTimeMs,
        DeltaX = row.DeltaX,
        DeltaY = row.DeltaY,
        DeltaTheta = row.DeltaTheta,
        MatchScore1 = row.MatchScore1,
        MatchScore2 = row.MatchScore2,
        NgReason = row.NgReason,
        Shift = row.Shift,
        CreatedAt = SqliteTime.FromDb(row.CreatedAt)
    };

    /// <summary>统计查询的行结果。</summary>
    private sealed class StatisticsRow
    {
        public int TotalCount { get; set; }
        public int OkCount { get; set; }
        public double AverageCycleTimeMs { get; set; }
    }

    /// <summary>OutputRecord 表的一行（明细查询与导出共用）。</summary>
    private sealed class OutputRow
    {
        public int Id { get; set; }
        public int SequenceNo { get; set; }
        public int RecipeId { get; set; }
        public string RecipeName { get; set; } = "";
        public string Result { get; set; } = "";
        public int CycleTimeMs { get; set; }
        public double DeltaX { get; set; }
        public double DeltaY { get; set; }
        public double DeltaTheta { get; set; }
        public double MatchScore1 { get; set; }
        public double MatchScore2 { get; set; }
        public string? NgReason { get; set; }
        public string Shift { get; set; } = "";
        public string CreatedAt { get; set; } = "";
    }
}
