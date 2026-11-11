// 职责：标定记录仓储的 SQLite + Dapper 实现（落库置 active、查询当前生效标定与历史）
// 层：Data/Repositories
// 依赖：SqliteConnectionFactory、ICalibrationRepository、Dapper

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Data.Repositories;

/// <summary>
/// 标定记录仓储实现。表结构见 <c>Calibration</c>；<see cref="Insert"/> 在事务内先清空旧 active 再写入新记录，
/// 保证「当前生效标定」始终唯一。
/// </summary>
public sealed class CalibrationRepository : ICalibrationRepository
{
    private const string SelectColumns = "Id, MatrixA, MatrixB, MatrixC, MatrixD, MatrixE, MatrixF, RmsError, PointCount, ImageFolder, IsActive, CreatedAt, CreatedBy";

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly ILogger<CalibrationRepository> _log;

    /// <summary>构造标定仓储。</summary>
    /// <param name="connectionFactory">连接工厂。</param>
    /// <param name="clock">时钟（写入 CreatedAt）。</param>
    /// <param name="log">日志。</param>
    public CalibrationRepository(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        ILogger<CalibrationRepository> log)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _log = log;
    }

    /// <inheritdoc />
    public void Insert(Calibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);

        if (calibration.AffineMatrix is null || calibration.AffineMatrix.Length != Calibration.AffineElementCount)
        {
            throw new ArgumentException($"仿射矩阵必须为 {Calibration.AffineElementCount} 个元素");
        }

        var m = calibration.AffineMatrix;

        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        // 先清空旧 active，再插入新记录并置 active，保证任何时刻最多一条激活标定
        connection.Execute(
            "UPDATE Calibration SET IsActive = 0 WHERE IsActive = 1;",
            transaction: transaction);

        var now = SqliteTime.ToDb(_clock.Now);

        connection.Execute(
            """
            INSERT INTO Calibration
                (MatrixA, MatrixB, MatrixC, MatrixD, MatrixE, MatrixF,
                 RmsError, PointCount, ImageFolder, IsActive, CreatedAt, CreatedBy)
            VALUES
                (@MatrixA, @MatrixB, @MatrixC, @MatrixD, @MatrixE, @MatrixF,
                 @RmsError, @PointCount, @ImageFolder, 1, @CreatedAt, @CreatedBy);
            """,
            new
            {
                MatrixA = m[0], MatrixB = m[1], MatrixC = m[2],
                MatrixD = m[3], MatrixE = m[4], MatrixF = m[5],
                calibration.RmsError,
                calibration.PointCount,
                calibration.ImageFolder,
                CreatedAt = now,
                calibration.CreatedBy
            },
            transaction);

        transaction.Commit();

        _log.LogInformation("标定结果已落库并置为当前生效：RMS={Rms:F4}mm 点数={PointCount} 图片目录={ImageFolder}",
                            calibration.RmsError, calibration.PointCount, calibration.ImageFolder);
    }

    /// <inheritdoc />
    public Calibration? GetActive()
    {
        using var connection = _connectionFactory.Create();
        var row = connection.QueryFirstOrDefault<CalibrationRow>(
            $"""
            SELECT {SelectColumns} FROM Calibration
            WHERE IsActive = 1
            ORDER BY Id DESC LIMIT 1;
            """);

        return row is null ? null : Map(row);
    }

    /// <inheritdoc />
    public IReadOnlyList<Calibration> GetRecent(int count)
    {
        using var connection = _connectionFactory.Create();
        var rows = connection.Query<CalibrationRow>(
            $"""
            SELECT {SelectColumns} FROM Calibration
            ORDER BY Id DESC LIMIT @Count;
            """,
            new { Count = count });

        return rows.Select(Map).ToList();
    }

    private static Calibration Map(CalibrationRow row) => new()
    {
        Id = row.Id,
        AffineMatrix = new[] { row.MatrixA, row.MatrixB, row.MatrixC, row.MatrixD, row.MatrixE, row.MatrixF },
        RmsError = row.RmsError,
        PointCount = row.PointCount,
        ImageFolder = row.ImageFolder,
        IsActive = row.IsActive != 0,
        CreatedAt = SqliteTime.FromDb(row.CreatedAt),
        CreatedBy = row.CreatedBy
    };

    /// <summary>Calibration 表的一行。</summary>
    private sealed class CalibrationRow
    {
        public int Id { get; set; }
        public double MatrixA { get; set; }
        public double MatrixB { get; set; }
        public double MatrixC { get; set; }
        public double MatrixD { get; set; }
        public double MatrixE { get; set; }
        public double MatrixF { get; set; }
        public double RmsError { get; set; }
        public int PointCount { get; set; }
        public string ImageFolder { get; set; } = "";
        public long IsActive { get; set; }
        public string CreatedAt { get; set; } = "";
        public string CreatedBy { get; set; } = "";
    }
}
