// 职责：按停机原因汇总的次数与总时长（产量页「停机分析」区的数据行）
// 层：Core/Models
// 依赖：DowntimeReasons

namespace AutoTaping.Core.Models;

/// <summary>
/// 停机分析的一行：某个原因在查询区间内的次数与合计时长。
/// </summary>
/// <param name="Reason">停机原因代码。</param>
/// <param name="Count">发生次数。</param>
/// <param name="TotalSeconds">合计停机秒数。</param>
public sealed record DowntimeSummary(string Reason, int Count, int TotalSeconds)
{
    /// <summary>停机原因中文名。</summary>
    public string DisplayName => DowntimeReasons.GetDisplayName(Reason);

    /// <summary>合计时长文本，形如 <c>08:23</c>（08-页面清单 页面 6 的显示格式）。</summary>
    public string TotalText => $"{TotalSeconds / 3600:D2}:{TotalSeconds % 3600 / 60:D2}";
}
