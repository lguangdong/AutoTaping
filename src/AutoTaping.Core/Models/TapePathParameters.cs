// 职责：贴附路径参数分组（直线 / 折线 / 圆弧），对应 JSON 的 tapePath 节点
// 层：Core/Models
// 依赖：TapePathMode、TapePathPoint

namespace AutoTaping.Core.Models;

/// <summary>
/// 贴附路径参数。UI 上由技术员按产品手工配置，不做 CAD 导入（见 00-项目总览 第 4 节）。
/// </summary>
public sealed class TapePathParameters
{
    /// <summary>路径模式，默认直线。</summary>
    public TapePathMode Mode { get; set; } = TapePathMode.Line;

    /// <summary>路径点（相对贴附基准的偏移量）。折线模式至少 2 点。</summary>
    public List<TapePathPoint> Points { get; set; } =
    [
        new TapePathPoint(0.0, 0.0, 2.0),
        new TapePathPoint(30.0, 0.0, 2.0)
    ];

    /// <summary>圆弧圆心，仅 <see cref="TapePathMode.Arc"/> 模式使用；直线/折线为 <see langword="null"/>。</summary>
    public TapePathPoint? ArcCenter { get; set; }

    /// <summary>圆弧半径（mm），仅圆弧模式使用。</summary>
    public double ArcRadius { get; set; }

    /// <summary>圆弧起始角（°）。</summary>
    public double ArcStartDeg { get; set; }

    /// <summary>圆弧终止角（°）。</summary>
    public double ArcEndDeg { get; set; }
}
