// 职责：产量统计服务抽象——计数、班次汇总、停机分类与明细查询，产量页只依赖本接口
// 层：Core/Abstractions
// 依赖：OutputDaySummary、HourlyOutput、DowntimeSummary、OutputRecord、ShiftProduction（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.Core.Abstractions;

/// <summary>
/// 产量统计服务。统计口径见 <c>docs/06-数据模型与数据库.md</c> 第 6 节与
/// <c>docs/05-核心状态机与流程.md</c> 第 7 节（节拍、停机分类、UPH）。
/// 本接口只做查询与汇总，产出记录的写入由流程的 <c>RecordOutputStep</c> 负责。
/// </summary>
public interface IOutputService
{
    /// <summary>取某一天的产量汇总（总数 / 良率 / 平均节拍 / 运行时长 / 停机时长 / 班次分解）。</summary>
    /// <param name="date">日期（只用日期部分）。</param>
    OutputDaySummary GetDaySummary(DateTime date);

    /// <summary>取某一天按小时聚合的产量（0 ~ 23 点，无数据的小时为 0）。</summary>
    /// <param name="date">日期（只用日期部分）。</param>
    IReadOnlyList<HourlyOutput> GetHourlyOutput(DateTime date);

    /// <summary>取某一天按班次聚合的产量（夜班 / 白班 / 中班）。</summary>
    /// <param name="date">日期（只用日期部分）。</param>
    IReadOnlyList<ShiftProduction> GetShiftSummaries(DateTime date);

    /// <summary>取区间内按停机原因汇总的次数与时长（08-页面清单 页面 6「停机分析」）。</summary>
    /// <param name="from">起始时间（含）。</param>
    /// <param name="to">结束时间（含）。</param>
    IReadOnlyList<DowntimeSummary> GetDowntimeSummary(DateTime from, DateTime to);

    /// <summary>取明细记录（按时间倒序，最多 <paramref name="maxCount"/> 条）。</summary>
    /// <param name="from">起始时间（含）。</param>
    /// <param name="to">结束时间（含）。</param>
    /// <param name="shift">班次过滤；<see langword="null"/> 表示全部班次。</param>
    /// <param name="maxCount">最多返回条数。</param>
    IReadOnlyList<OutputRecord> GetRecords(DateTime from, DateTime to, string? shift, int maxCount);

    /// <summary>记录一条停机（原因分类见 <see cref="DowntimeReasons"/>）。</summary>
    /// <param name="reason">停机原因代码。</param>
    /// <param name="startTime">停机开始时间。</param>
    /// <param name="endTime">恢复时间；未知（如断电补记）传 <see langword="null"/>。</param>
    void RecordDowntime(string reason, DateTime startTime, DateTime? endTime);

    /// <summary>按产出记录重算并写入当前班次的班次汇总（幂等，可反复调用）。</summary>
    /// <param name="time">时刻（决定班次）。</param>
    void RefreshShiftProduction(DateTime time);

    /// <summary>取某个时刻所属的班次代码。</summary>
    /// <param name="time">时刻。</param>
    string GetShiftName(DateTime time);

    /// <summary>导出区间内产出明细为 CSV（UTF-8 with BOM）。</summary>
    /// <param name="from">起始时间（含）。</param>
    /// <param name="to">结束时间（含）。</param>
    /// <param name="path">目标 CSV 文件路径。</param>
    void ExportCsv(DateTime from, DateTime to, string path);
}
