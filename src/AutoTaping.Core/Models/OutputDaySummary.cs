// 职责：某一天的产量汇总值对象（统计卡片 + 班次分解），产量页一次性展示用
// 层：Core/Models
// 依赖：ShiftProduction

namespace AutoTaping.Core.Models;

/// <summary>
/// 单日产量汇总。统计口径见 <c>docs/06-数据模型与数据库.md</c> 第 6 节：
/// 良率 = OK / 总数，平均节拍 = 平均 <c>CycleTimeMs</c>，运行时长按实际生产节拍累计。
/// </summary>
public sealed class OutputDaySummary
{
    /// <summary>统计日期（只用日期部分）。</summary>
    public DateTime Date { get; init; }

    /// <summary>总产出件数。</summary>
    public int TotalCount { get; init; }

    /// <summary>良品件数。</summary>
    public int OkCount { get; init; }

    /// <summary>不良品件数。</summary>
    public int NgCount => TotalCount - OkCount;

    /// <summary>良率（0 ~ 1）；无数据时为 0。</summary>
    public double Yield => TotalCount == 0 ? 0 : (double)OkCount / TotalCount;

    /// <summary>平均节拍（ms）；无数据时为 0。</summary>
    public double AverageCycleTimeMs { get; init; }

    /// <summary>运行时长（秒）：当日各件节拍之和，即真实生产时间。</summary>
    public int RunSeconds { get; init; }

    /// <summary>停机时长（秒）：当日停机记录合计。</summary>
    public int DowntimeSeconds { get; init; }

    /// <summary>班次分解（夜班 / 白班 / 中班）。</summary>
    public IReadOnlyList<ShiftProduction> Shifts { get; init; } = Array.Empty<ShiftProduction>();
}
