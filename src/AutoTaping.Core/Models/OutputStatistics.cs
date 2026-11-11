// 职责：产量统计结果值对象（区间总数 / 良品数 / 平均节拍 / 良率）
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 产量统计结果，对应 <c>docs/06-数据模型与数据库.md</c> 第 6 节「当班良率」查询的输出。
/// </summary>
public sealed class OutputStatistics
{
    /// <summary>区间内总产出件数。</summary>
    public int TotalCount { get; init; }

    /// <summary>区间内良品件数。</summary>
    public int OkCount { get; init; }

    /// <summary>区间内不良品件数。</summary>
    public int NgCount => TotalCount - OkCount;

    /// <summary>区间内平均节拍（ms）；无数据时为 0。</summary>
    public double AverageCycleTimeMs { get; init; }

    /// <summary>良率（0 ~ 1）；无数据时为 0。</summary>
    public double Yield => TotalCount == 0 ? 0 : (double)OkCount / TotalCount;
}
