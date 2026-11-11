// 职责：DailyMaintenanceService 单元测试——过期数据先归档 CSV 再删除、标定裁剪、过期日志清理（S9 每日维护）
// 层：Tests
// 依赖：AutoTaping.Data（DailyMaintenanceService）、Tests/Infrastructure/TestDatabase、Dapper

using System.Globalization;
using System.IO;
using AutoTaping.Core.Models;
using AutoTaping.Data;
using AutoTaping.Tests.Infrastructure;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTaping.Tests;

/// <summary>
/// <see cref="DailyMaintenanceService"/> 的测试。时间用相对区间构造，避免依赖具体日期；
/// 归档目录与日志目录都指向临时目录，测试结束随测试库目录一起删除。
/// </summary>
public sealed class DailyMaintenanceServiceTests
{
    /// <summary>数据库时间列格式（06 文档第 10 节第 5 条）。</summary>
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    [Fact]
    public void RunNow_ArchivesExpiredOutputRecordsToCsvWithBom_AndDeletesThem()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var now = DateTime.Now;
        InsertOutputRecord(database, sequenceNo: 1, createdAt: now.AddDays(-400));
        InsertOutputRecord(database, sequenceNo: 2, createdAt: now.AddDays(-1));

        var archiveDirectory = CreateArchiveDirectory(database.DatabasePath);
        var service = CreateService(database, new MaintenanceOptions { ArchiveDirectory = archiveDirectory });

        var result = service.RunNow();

        Assert.Equal(1, result.ArchivedOutputCount);
        Assert.Single(result.ArchiveFiles);
        Assert.True(File.Exists(result.ArchiveFiles[0]));

        // UTF-8 with BOM：Excel 打开中文不乱码（06 文档第 9 节）
        var bytes = File.ReadAllBytes(result.ArchiveFiles[0]);
        Assert.True(bytes.Length > 3);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);

        Assert.Equal(1, CountRows(database, "OutputRecord"));
    }

    [Fact]
    public void RunNow_KeepsRecentRecords_WhenNothingIsExpired()
    {
        using var database = new TestDatabase();
        database.Initialize();

        InsertOutputRecord(database, sequenceNo: 1, createdAt: DateTime.Now.AddDays(-1));

        var service = CreateService(database, new MaintenanceOptions
        {
            ArchiveDirectory = CreateArchiveDirectory(database.DatabasePath)
        });

        var result = service.RunNow();

        Assert.Equal(0, result.ArchivedOutputCount);
        Assert.Empty(result.ArchiveFiles);
        Assert.Equal(1, CountRows(database, "OutputRecord"));
    }

    [Fact]
    public void RunNow_PrunesCalibrationBeyondKeepCount()
    {
        using var database = new TestDatabase();
        database.Initialize();

        // 种子已有一条标定，再补 3 条更早的
        for (var i = 0; i < 3; i++)
        {
            InsertCalibration(database, i + 1, DateTime.Now.AddDays(-10 - i));
        }

        var service = CreateService(database, new MaintenanceOptions
        {
            ArchiveDirectory = CreateArchiveDirectory(database.DatabasePath),
            CalibrationKeepCount = 2
        });

        var result = service.RunNow();

        Assert.Equal(2, result.DeletedCalibrationCount);
        Assert.Equal(2, CountRows(database, "Calibration"));
    }

    [Fact]
    public void RunNow_DeletesExpiredLogFilesOnly()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var logDirectory = CreateArchiveDirectory(database.DatabasePath);
        var expired = Path.Combine(logDirectory, "autotaping-20260101.log");
        var fresh = Path.Combine(logDirectory, "autotaping-20261108.log");
        File.WriteAllText(expired, "old");
        File.WriteAllText(fresh, "new");
        File.SetLastWriteTime(expired, DateTime.Now.AddDays(-200));

        var service = CreateService(database, new MaintenanceOptions
        {
            ArchiveDirectory = CreateArchiveDirectory(database.DatabasePath),
            LogDirectory = logDirectory,
            LogRetainDays = 90
        });

        var result = service.RunNow();

        Assert.Equal(1, result.DeletedLogFileCount);
        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void RunNow_WritesAlarmArchiveWithChineseLevel()
    {
        using var database = new TestDatabase();
        database.Initialize();

        using (var connection = database.OpenConnection())
        {
            connection.Execute(
                """
                INSERT INTO AlarmRecord (Code, Message, Level, Source, StartTime, IsAcked)
                VALUES ('AL301', '真空吸不住', 3, '贴附头', @StartTime, 0);
                """,
                new { StartTime = DateTime.Now.AddDays(-400).ToString(TimeFormat, CultureInfo.InvariantCulture) });
        }

        var service = CreateService(database, new MaintenanceOptions
        {
            ArchiveDirectory = CreateArchiveDirectory(database.DatabasePath)
        });

        var result = service.RunNow();

        Assert.Equal(1, result.ArchivedAlarmCount);
        var alarmArchive = result.ArchiveFiles.Single(path => Path.GetFileName(path).StartsWith("AlarmRecord", StringComparison.Ordinal));
        Assert.Contains("严重", File.ReadAllText(alarmArchive));
        Assert.Equal(0, CountRows(database, "AlarmRecord"));
    }

    private static DailyMaintenanceService CreateService(TestDatabase database, MaintenanceOptions options)
        => new(database.ConnectionFactory, database.Clock, options, NullLogger<DailyMaintenanceService>.Instance);

    /// <summary>在测试库目录下建一个归档目录（随测试库一起删除，不留垃圾）。</summary>
    private static string CreateArchiveDirectory(string databasePath)
    {
        var directory = Path.Combine(Path.GetDirectoryName(databasePath)!, "archive");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void InsertOutputRecord(TestDatabase database, int sequenceNo, DateTime createdAt)
    {
        using var connection = database.OpenConnection();
        connection.Execute(
            """
            INSERT INTO OutputRecord
                (SequenceNo, RecipeId, RecipeName, Result, CycleTimeMs, DeltaX, DeltaY, DeltaTheta,
                 MatchScore1, MatchScore2, NgReason, Shift, CreatedAt)
            VALUES
                (@SequenceNo, 1, '示例配方', 'OK', 3000, 0.03, -0.02, 0.15, 0.92, 0.9, NULL, 'DAY', @CreatedAt);
            """,
            new
            {
                SequenceNo = sequenceNo,
                CreatedAt = createdAt.ToString(TimeFormat, CultureInfo.InvariantCulture)
            });
    }

    private static void InsertCalibration(TestDatabase database, int index, DateTime createdAt)
    {
        using var connection = database.OpenConnection();
        connection.Execute(
            """
            INSERT INTO Calibration
                (MatrixA, MatrixB, MatrixC, MatrixD, MatrixE, MatrixF,
                 RmsError, PointCount, ImageFolder, IsActive, CreatedAt, CreatedBy)
            VALUES
                (0.25, 0, 50, 0, -0.25, 325, 0.02, 9, @ImageFolder, 0, @CreatedAt, 'admin');
            """,
            new
            {
                ImageFolder = $"images/calibration/test-{index}",
                CreatedAt = createdAt.ToString(TimeFormat, CultureInfo.InvariantCulture)
            });
    }

    private static int CountRows(TestDatabase database, string table)
    {
        using var connection = database.OpenConnection();
        return connection.ExecuteScalar<int>($"SELECT COUNT(1) FROM {table};");
    }
}
