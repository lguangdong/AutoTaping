// 职责：产量统计服务——计数、小时聚合、班次汇总、停机分类与明细查询（产量页的数据来源）
// 层：Core/Services
// 依赖：IOutputRepository/IDowntimeRepository/IShiftProductionRepository/IClock（Core/Abstractions）、
//       OutputRecord/HourlyOutput/ShiftProduction/DowntimeRecord/DowntimeSummary/ShiftSchedule（Core/Models）

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Core.Services;

/// <summary>
/// <see cref="IOutputService"/> 的默认实现。统计口径：
/// <list type="bullet">
/// <item>良率 = OK 件数 / 总件数（06 文档第 6 节）。</item>
/// <item>平均节拍 = 平均 <c>CycleTimeMs</c>；运行时长 = 当日各件节拍之和（即真实生产时间）。</item>
/// <item>停机时长按 <c>DowntimeRecord</c> 的时长累加，分类口径见 05 文档 7.2 节。</item>
/// </list>
/// 本类只读产出记录与停机记录，不做任何界面操作；查询全部在调用线程上完成（ViewModel 负责丢到后台线程）。
/// </summary>
public sealed class OutputService : IOutputService
{
    /// <summary>小时数（0 ~ 23），小时柱状图固定 24 根柱子。</summary>
    private const int HoursPerDay = 24;

    private readonly IOutputRepository _outputs;
    private readonly IDowntimeRepository _downtimes;
    private readonly IShiftProductionRepository _shifts;
    private readonly IClock _clock;
    private readonly ILogger<OutputService> _log;

    /// <summary>构造产量统计服务。</summary>
    /// <param name="outputs">产出记录仓储。</param>
    /// <param name="downtimes">停机记录仓储。</param>
    /// <param name="shifts">班次汇总仓储。</param>
    /// <param name="clock">时钟。</param>
    /// <param name="log">日志。</param>
    public OutputService(
        IOutputRepository outputs,
        IDowntimeRepository downtimes,
        IShiftProductionRepository shifts,
        IClock clock,
        ILogger<OutputService> log)
    {
        _outputs = outputs;
        _downtimes = downtimes;
        _shifts = shifts;
        _clock = clock;
        _log = log;
    }

    /// <inheritdoc />
    public OutputDaySummary GetDaySummary(DateTime date)
    {
        var (start, end) = GetDayRange(date);
        var records = _outputs.Query(start, end, shift: null, maxCount: int.MaxValue);

        var total = records.Count;
        var ok = records.Count(record => string.Equals(record.Result, OutputRecord.ResultOk, StringComparison.Ordinal));
        var runSeconds = (int)Math.Round(records.Sum(record => record.CycleTimeMs) / 1000.0);
        var averageCycleMs = total == 0 ? 0 : records.Average(record => (double)record.CycleTimeMs);
        var downtimeSeconds = _downtimes.Query(start, end).Sum(record => record.DurationSeconds);

        return new OutputDaySummary
        {
            Date = start,
            TotalCount = total,
            OkCount = ok,
            AverageCycleTimeMs = averageCycleMs,
            RunSeconds = runSeconds,
            DowntimeSeconds = downtimeSeconds,
            Shifts = BuildShiftSummaries(start, records)
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<HourlyOutput> GetHourlyOutput(DateTime date)
    {
        var (start, end) = GetDayRange(date);
        var records = _outputs.Query(start, end, shift: null, maxCount: int.MaxValue);

        var buckets = new int[HoursPerDay];
        var okBuckets = new int[HoursPerDay];

        foreach (var record in records)
        {
            var hour = record.CreatedAt.Hour;
            buckets[hour]++;
            if (string.Equals(record.Result, OutputRecord.ResultOk, StringComparison.Ordinal))
            {
                okBuckets[hour]++;
            }
        }

        var result = new List<HourlyOutput>(HoursPerDay);
        for (var hour = 0; hour < HoursPerDay; hour++)
        {
            result.Add(new HourlyOutput(hour, buckets[hour], okBuckets[hour]));
        }

        return result;
    }

    /// <inheritdoc />
    public IReadOnlyList<ShiftProduction> GetShiftSummaries(DateTime date)
    {
        var (start, end) = GetDayRange(date);
        return BuildShiftSummaries(start, _outputs.Query(start, end, shift: null, maxCount: int.MaxValue));
    }

    /// <inheritdoc />
    public IReadOnlyList<DowntimeSummary> GetDowntimeSummary(DateTime from, DateTime to)
    {
        var records = _downtimes.Query(from, to);

        var groups = records
            .GroupBy(record => record.Reason, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new DowntimeSummary(group.Key, group.Count(), group.Sum(record => record.DurationSeconds)),
                StringComparer.Ordinal);

        // 固定按 05 文档 7.2 节的原因顺序输出（含 0 次的），界面行不会随数据有无而跳动
        var result = new List<DowntimeSummary>(DowntimeReasons.All.Count);
        foreach (var reason in DowntimeReasons.All)
        {
            result.Add(groups.TryGetValue(reason, out var summary) ? summary : new DowntimeSummary(reason, 0, 0));
        }

        // 文档之外的未知原因（数据被外部写入）追加在最后，不丢数据
        foreach (var pair in groups.Where(pair => IndexOfReason(pair.Key) >= DowntimeReasons.All.Count))
        {
            result.Add(pair.Value);
        }

        return result;
    }

    /// <inheritdoc />
    public IReadOnlyList<OutputRecord> GetRecords(DateTime from, DateTime to, string? shift, int maxCount)
        => _outputs.Query(from, to, shift, maxCount);

    /// <inheritdoc />
    public void RecordDowntime(string reason, DateTime startTime, DateTime? endTime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var durationSeconds = 0;
        if (endTime.HasValue)
        {
            var seconds = (endTime.Value - startTime).TotalSeconds;
            durationSeconds = seconds <= 0 ? 0 : (int)Math.Round(seconds);
        }

        var record = new DowntimeRecord
        {
            Reason = reason,
            StartTime = startTime,
            EndTime = endTime,
            DurationSeconds = durationSeconds
        };

        try
        {
            _downtimes.Insert(record);
            _log.LogInformation("停机记录：{Reason}（{DisplayName}），开始 {Start:HH:mm:ss}，时长 {Seconds}s",
                                reason, DowntimeReasons.GetDisplayName(reason), startTime, durationSeconds);
        }
        catch (Exception ex)
        {
            // 停机统计写失败不影响生产（05 文档 5 节：记录失败要日志，不报警）
            _log.LogError(ex, "停机记录写入失败：{Reason}", reason);
        }
    }

    /// <inheritdoc />
    public void RefreshShiftProduction(DateTime time)
    {
        var (start, end) = ShiftSchedule.GetShiftWindow(time);
        var records = _outputs.Query(start, end, shift: null, maxCount: int.MaxValue);

        var ok = records.Count(record => string.Equals(record.Result, OutputRecord.ResultOk, StringComparison.Ordinal));
        var downSeconds = _downtimes.Query(start, end).Sum(record => record.DurationSeconds);

        var summary = new ShiftProduction
        {
            ShiftDate = start.Date,
            ShiftName = ShiftSchedule.GetShiftName(time),
            TotalCount = records.Count,
            OkCount = ok,
            NgCount = records.Count - ok,
            RunSeconds = (int)Math.Round(records.Sum(record => record.CycleTimeMs) / 1000.0),
            DownSeconds = downSeconds,
            LastUpdateAt = _clock.Now
        };

        try
        {
            _shifts.Upsert(summary);
            _log.LogDebug("班次汇总已刷新：{Date:yyyy-MM-dd} {Shift}，产出 {Total} 件",
                          summary.ShiftDate, summary.ShiftName, summary.TotalCount);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "班次汇总写入失败：{Shift}", summary.ShiftName);
        }
    }

    /// <inheritdoc />
    public string GetShiftName(DateTime time) => ShiftSchedule.GetShiftName(time);

    /// <inheritdoc />
    public void ExportCsv(DateTime from, DateTime to, string path) => _outputs.ExportCsv(from, to, path);

    /// <summary>取某一天的 [00:00:00, 23:59:59] 区间（仓储的时间比较是闭区间）。</summary>
    private static (DateTime Start, DateTime End) GetDayRange(DateTime date)
        => (date.Date, date.Date.AddDays(1).AddSeconds(-1));

    /// <summary>把产出记录按班次分组（夜 / 白 / 中，按时间顺序输出，无记录的班次也返回）。</summary>
    private static IReadOnlyList<ShiftProduction> BuildShiftSummaries(DateTime date, IReadOnlyList<OutputRecord> records)
    {
        var result = new List<ShiftProduction>(3);

        foreach (var shift in ShiftSchedule.All)
        {
            var group = records.Where(record => string.Equals(record.Shift, shift, StringComparison.Ordinal)).ToList();
            var ok = group.Count(record => string.Equals(record.Result, OutputRecord.ResultOk, StringComparison.Ordinal));

            result.Add(new ShiftProduction
            {
                ShiftDate = date.Date,
                ShiftName = shift,
                TotalCount = group.Count,
                OkCount = ok,
                NgCount = group.Count - ok,
                RunSeconds = (int)Math.Round(group.Sum(record => record.CycleTimeMs) / 1000.0),
                LastUpdateAt = date
            });
        }

        return result;
    }

    private static int IndexOfReason(string reason)
    {
        for (var i = 0; i < DowntimeReasons.All.Count; i++)
        {
            if (string.Equals(DowntimeReasons.All[i], reason, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return DowntimeReasons.All.Count;
    }
}
