// 职责：按小时聚合的产量（产量页小时柱状图的数据点）
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 某一小时的产量聚合。柱状图用 <see cref="Count"/> 换算柱高，不引入第三方图表库
/// （07-UI设计规范 第 10 节、08-页面清单 页面 6）。
/// </summary>
/// <param name="Hour">小时（0 ~ 23）。</param>
/// <param name="Count">该小时总产出件数。</param>
/// <param name="OkCount">该小时良品件数。</param>
public sealed record HourlyOutput(int Hour, int Count, int OkCount)
{
    /// <summary>横轴标签，形如 <c>08</c>。</summary>
    public string HourText => Hour.ToString("D2");
}
