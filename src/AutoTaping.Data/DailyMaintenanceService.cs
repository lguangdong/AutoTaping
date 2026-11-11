// 职责：每日维护实现——过期数据先归档成 CSV 再删除、标定记录裁剪（含原图目录）、过期日志清理
// 层：Data
// 依赖：SqliteConnectionFactory、CsvWriter、SqliteTime、IClock（Core/Abstractions）、
//       MaintenanceOptions/MaintenanceResult（Core/Models）、Dapper

using System.Globalization;
using System.IO;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Data;

/// <summary>
/// <see cref="IMaintenanceService"/> 的默认实现，保留策略与 <c>docs/06-数据模型与数据库.md</c> 第 7 节一致。
/// <para>
/// 执行顺序固定为「先写归档 CSV → 再删库」，任何一步失败都不会丢数据（最多重复归档一次）。
/// 后台循环每 5 分钟检查一次，到达配置的整点后当天只执行一次；系统设置页也可手动触发。
/// </para>
/// </summary>
public sealed class DailyMaintenanceService : IMaintenanceService
{
    /// <summary>
    /// 后台检查周期（分钟）。取 5 分钟而不是「等到明天 02:00 再醒」：长时间运行的工控机可能跨天、
    /// 可能被系统休眠打断，短周期轮询 + 「当天只跑一次」标记比一次性长延时更可靠。
    /// </summary>
    private const int TickMinutes = 5;

    /// <summary>日志文件的匹配模式（Serilog 按天滚动的文件名格式）。</summary>
    private const string LogFilePattern = "autotaping-*.log";

    /// <summary>归档文件名的表名前缀：产出记录。</summary>
    private const string OutputTable = "OutputRecord";

    /// <summary>归档文件名的表名前缀：报警记录。</summary>
    private const string AlarmTable = "AlarmRecord";

    /// <summary>归档文件名的表名前缀：操作日志。</summary>
    private const string OperationLogTable = "OperationLog";

    private static readonly IReadOnlyList<string> OutputHeader =
        new[] { "序号", "时间", "配方", "结果", "节拍(ms)", "ΔX(mm)", "ΔY(mm)", "Δθ(°)", "NG原因", "班次" };

    private static readonly IReadOnlyList<string> AlarmHeader =
        new[] { "代码", "内容", "等级", "来源", "开始时间", "确认时间", "确认人", "恢复时间" };

    private static readonly IReadOnlyList<string> OperationLogHeader =
        new[] { "用户", "动作", "对象", "旧值", "新值", "时间" };

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly MaintenanceOptions _options;
    private readonly ILogger<DailyMaintenanceService> _log;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private DateTime _lastRunDate = DateTime.MinValue;
    private bool _disposed;

    /// <summary>串行化维护执行（后台定时 + 界面手动按钮可能同时触发）。</summary>
    private readonly object _runSync = new();

    /// <summary>构造维护服务。</summary>
    /// <param name="connectionFactory">连接工厂。</param>
    /// <param name="clock">时钟。</param>
    /// <param name="options">维护配置（保留天数、执行时刻、目录）。</param>
    /// <param name="log">日志。</param>
    public DailyMaintenanceService(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        MaintenanceOptions options,
        ILogger<DailyMaintenanceService> log)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _options = options;
        _log = log;
    }

    /// <inheritdoc />
    public DateTime? LastRunAt { get; private set; }

    /// <inheritdoc />
    public void Start()
    {
        if (_disposed || _loopTask is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        _log.LogInformation("每日维护任务已启动：每天 {Hour}:00 执行，归档目录 {Directory}",
                            _options.RunHourOfDay, ResolveDirectory(_options.ArchiveDirectory));
    }

    /// <inheritdoc />
    public void Stop()
    {
        var cts = _cts;
        if (cts is null)
        {
            return;
        }

        _cts = null;
        cts.Cancel();
        cts.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    /// <inheritdoc />
    public MaintenanceResult RunNow()
    {
        // 后台定时与「清理旧数据」按钮可能同时触发：串行化，避免同一批记录被归档两次
        lock (_runSync)
        {
            var now = _clock.Now;
            var archiveDirectory = ResolveDirectory(_options.ArchiveDirectory);
            Directory.CreateDirectory(archiveDirectory);

            var archiveFiles = new List<string>();

            using var connection = _connectionFactory.Create();
            var archivedOutputs = ArchiveOutputRecords(connection, now, archiveDirectory, archiveFiles);
            var archivedAlarms = ArchiveAlarmRecords(connection, now, archiveDirectory, archiveFiles);
            var archivedLogs = ArchiveOperationLogs(connection, now, archiveDirectory, archiveFiles);
            var deletedCalibrations = PruneCalibrations(connection, archiveDirectory);
            var deletedLogFiles = PruneLogFiles(now);

            LastRunAt = now;

            var result = new MaintenanceResult(
                now, archivedOutputs, archivedAlarms, archivedLogs, deletedCalibrations, deletedLogFiles, archiveFiles);

            _log.LogInformation(
                "每日维护完成：归档产出 {Output} 条 / 报警 {Alarm} 条 / 操作日志 {Log} 条，删除标定 {Calibration} 条、日志文件 {LogFiles} 个",
                archivedOutputs, archivedAlarms, archivedLogs, deletedCalibrations, deletedLogFiles);

            return result;
        }
    }

    /// <summary>后台循环：每 <see cref="TickMinutes"/> 分钟检查一次是否到达执行时刻，当天只执行一次。</summary>
    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(TickMinutes));

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = _clock.Now;
                if (now.Hour != _options.RunHourOfDay || now.Date == _lastRunDate)
                {
                    continue;
                }

                _lastRunDate = now.Date;

                try
                {
                    RunNow();
                }
                catch (Exception ex)
                {
                    // 后台任务异常不得让线程退出、更不得影响生产
                    _log.LogError(ex, "每日维护执行失败，将在下一个周期重试（当天不再重复）");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _log.LogDebug("每日维护任务已停止");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "每日维护循环异常终止");
        }
    }

    /// <summary>归档并删除过期产出记录（CSV 格式见 06 文档 9.1 节）。</summary>
    private int ArchiveOutputRecords(SqliteConnection connection, DateTime now, string archiveDirectory, List<string> archiveFiles)
    {
        var cutoff = SqliteTime.ToDb(now.AddDays(-_options.OutputRetainDays));

        var rows = connection.Query<ArchiveOutputRow>(
            $"""
            SELECT SequenceNo, CreatedAt, RecipeName, Result, CycleTimeMs,
                   DeltaX, DeltaY, DeltaTheta, NgReason, Shift
            FROM {OutputTable}
            WHERE CreatedAt < @Cutoff
            ORDER BY CreatedAt;
            """,
            new { Cutoff = cutoff }).ToList();

        if (rows.Count == 0)
        {
            return 0;
        }

        var path = BuildArchivePath(archiveDirectory, OutputTable, now);
        CsvWriter.Write(path, OutputHeader, rows.Select(row => new[]
        {
            row.SequenceNo.ToString(CultureInfo.InvariantCulture),
            row.CreatedAt,
            row.RecipeName,
            row.Result,
            row.CycleTimeMs.ToString(CultureInfo.InvariantCulture),
            row.DeltaX.ToString("F3", CultureInfo.InvariantCulture),
            row.DeltaY.ToString("F3", CultureInfo.InvariantCulture),
            row.DeltaTheta.ToString("F3", CultureInfo.InvariantCulture),
            row.NgReason ?? string.Empty,
            row.Shift
        }));
        archiveFiles.Add(path);

        DeleteBefore(connection, OutputTable, cutoff);
        return rows.Count;
    }

    /// <summary>归档并删除过期报警记录（CSV 格式见 06 文档 9.2 节）。</summary>
    private int ArchiveAlarmRecords(SqliteConnection connection, DateTime now, string archiveDirectory, List<string> archiveFiles)
    {
        var cutoff = SqliteTime.ToDb(now.AddDays(-_options.AlarmRetainDays));

        var rows = connection.Query<ArchiveAlarmRow>(
            $"""
            SELECT Code, Message, Level, Source, StartTime, AckTime, AckUser, EndTime
            FROM {AlarmTable}
            WHERE StartTime < @Cutoff
            ORDER BY StartTime;
            """,
            new { Cutoff = cutoff }).ToList();

        if (rows.Count == 0)
        {
            return 0;
        }

        var path = BuildArchivePath(archiveDirectory, AlarmTable, now);
        CsvWriter.Write(path, AlarmHeader, rows.Select(row => new[]
        {
            row.Code,
            row.Message,
            DescribeLevel(row.Level),
            row.Source,
            row.StartTime,
            row.AckTime ?? string.Empty,
            row.AckUser ?? string.Empty,
            row.EndTime ?? string.Empty
        }));
        archiveFiles.Add(path);

        DeleteBefore(connection, AlarmTable, cutoff, timeColumn: "StartTime");
        return rows.Count;
    }

    /// <summary>归档并删除过期操作日志。</summary>
    private int ArchiveOperationLogs(SqliteConnection connection, DateTime now, string archiveDirectory, List<string> archiveFiles)
    {
        var cutoff = SqliteTime.ToDb(now.AddDays(-_options.OperationLogRetainDays));

        var rows = connection.Query<ArchiveOperationLogRow>(
            $"""
            SELECT UserName, Action, Target, OldValue, NewValue, CreatedAt
            FROM {OperationLogTable}
            WHERE CreatedAt < @Cutoff
            ORDER BY CreatedAt;
            """,
            new { Cutoff = cutoff }).ToList();

        if (rows.Count == 0)
        {
            return 0;
        }

        var path = BuildArchivePath(archiveDirectory, OperationLogTable, now);
        CsvWriter.Write(path, OperationLogHeader, rows.Select(row => new[]
        {
            row.UserName,
            row.Action,
            row.Target,
            row.OldValue ?? string.Empty,
            row.NewValue ?? string.Empty,
            row.CreatedAt
        }));
        archiveFiles.Add(path);

        DeleteBefore(connection, OperationLogTable, cutoff);
        return rows.Count;
    }

    /// <summary>标定记录只保留最近 N 条，超出的连同标定原图目录一起删除（06 文档第 7 节）。</summary>
    private int PruneCalibrations(SqliteConnection connection, string archiveDirectory)
    {
        if (_options.CalibrationKeepCount <= 0)
        {
            return 0;
        }

        var expired = connection.Query<ExpiredCalibrationRow>(
            """
            SELECT Id, ImageFolder FROM Calibration
            ORDER BY CreatedAt DESC, Id DESC
            LIMIT -1 OFFSET @Keep;
            """,
            new { Keep = _options.CalibrationKeepCount }).ToList();

        if (expired.Count == 0)
        {
            return 0;
        }

        using (var transaction = connection.BeginTransaction())
        {
            foreach (var item in expired)
            {
                connection.Execute("DELETE FROM Calibration WHERE Id = @Id;", new { item.Id }, transaction);
            }

            transaction.Commit();
        }

        // 原图目录可能很大，先删库后删文件：即便文件删不掉，记录也不会再引用它
        foreach (var item in expired)
        {
            DeleteImageFolder(item.ImageFolder, archiveDirectory);
        }

        return expired.Count;
    }

    /// <summary>清理超过保留天数的日志文件（Serilog 按文件数保留，这里按天数再兜一层）。</summary>
    private int PruneLogFiles(DateTime now)
    {
        var logDirectory = ResolveDirectory(_options.LogDirectory);
        if (!Directory.Exists(logDirectory))
        {
            return 0;
        }

        var deadline = now.AddDays(-_options.LogRetainDays);
        var deleted = 0;

        foreach (var file in Directory.GetFiles(logDirectory, LogFilePattern))
        {
            try
            {
                if (File.GetLastWriteTime(file) >= deadline)
                {
                    continue;
                }

                File.Delete(file);
                deleted++;
            }
            catch (Exception ex)
            {
                // 当天正在写的日志文件被 Serilog 持有，删不掉是正常的
                _log.LogWarning(ex, "删除过期日志文件失败：{File}", file);
            }
        }

        return deleted;
    }

    /// <summary>删除指定时间列早于截止时间的记录（单表单事务）。</summary>
    private static void DeleteBefore(SqliteConnection connection, string table, string cutoff, string timeColumn = "CreatedAt")
    {
        using var transaction = connection.BeginTransaction();
        connection.Execute($"DELETE FROM {table} WHERE {timeColumn} < @Cutoff;", new { Cutoff = cutoff }, transaction);
        transaction.Commit();
    }

    /// <summary>
    /// 删除标定原图目录。只允许删除程序目录下的目录：数据库被外部改写时不能因为一个路径就把别处的目录删了。
    /// </summary>
    private void DeleteImageFolder(string? folder, string archiveDirectory)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(Path.IsPathRooted(folder)
                ? folder
                : Path.Combine(AppContext.BaseDirectory, folder));

            var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            if (!fullPath.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath, baseDirectory, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(Path.GetFullPath(archiveDirectory), StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning("标定原图目录不在程序目录内或指向受保护目录，已跳过删除：{Folder}", fullPath);
                return;
            }

            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
                _log.LogInformation("已删除过期标定原图目录：{Folder}", fullPath);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "删除标定原图目录失败：{Folder}", folder);
        }
    }

    private static string BuildArchivePath(string archiveDirectory, string table, DateTime now)
        => Path.Combine(archiveDirectory, $"{table}_{now:yyyyMMdd_HHmmss}.csv");

    /// <summary>把配置里的相对目录转成相对程序目录的绝对路径。</summary>
    private static string ResolveDirectory(string directory)
        => Path.IsPathRooted(directory) ? directory : Path.Combine(AppContext.BaseDirectory, directory);

    /// <summary>报警等级显示名（09 文档第一节）。</summary>
    private static string DescribeLevel(int level) => level switch
    {
        1 => "提示",
        2 => "警告",
        3 => "严重",
        _ => level.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>OutputRecord 归档行。</summary>
    private sealed class ArchiveOutputRow
    {
        public long SequenceNo { get; set; }
        public string CreatedAt { get; set; } = "";
        public string RecipeName { get; set; } = "";
        public string Result { get; set; } = "";
        public long CycleTimeMs { get; set; }
        public double DeltaX { get; set; }
        public double DeltaY { get; set; }
        public double DeltaTheta { get; set; }
        public string? NgReason { get; set; }
        public string Shift { get; set; } = "";
    }

    /// <summary>AlarmRecord 归档行。</summary>
    private sealed class ArchiveAlarmRow
    {
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
        public int Level { get; set; }
        public string Source { get; set; } = "";
        public string StartTime { get; set; } = "";
        public string? AckTime { get; set; }
        public string? AckUser { get; set; }
        public string? EndTime { get; set; }
    }

    /// <summary>OperationLog 归档行。</summary>
    private sealed class ArchiveOperationLogRow
    {
        public string UserName { get; set; } = "";
        public string Action { get; set; } = "";
        public string Target { get; set; } = "";
        public string? OldValue { get; set; }
        public string? NewValue { get; set; }
        public string CreatedAt { get; set; } = "";
    }

    /// <summary>待裁剪的标定记录。</summary>
    private sealed class ExpiredCalibrationRow
    {
        public int Id { get; set; }
        public string? ImageFolder { get; set; }
    }
}
