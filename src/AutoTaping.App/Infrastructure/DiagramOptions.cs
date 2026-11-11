// 职责：主画面 2.5D 侧视示意图与实时曲线的量程参数（X/Z 轴软限位，mm）
// 层：App/Infrastructure
// 依赖：无（由 App 启动时从 appsettings.json 的 Hardware:MotionCard:Axes 填充后注册为单例）

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// 主画面示意图 / 曲线的坐标量程。来源：<c>appsettings.json</c> 的 <c>Hardware:MotionCard:Axes</c>
/// 中各轴软限位（10-编码约定 第八节：硬件参数进配置，不得写死在 ViewModel 里）。
/// </summary>
public sealed class DiagramOptions
{
    /// <summary>X 轴行程下限（mm），示意图左端与曲线纵轴下限。</summary>
    public double XMinLimit { get; init; }

    /// <summary>X 轴行程上限（mm），示意图右端与曲线纵轴上限。</summary>
    public double XMaxLimit { get; init; } = 400.0;

    /// <summary>Z 轴行程下限（mm），示意图最低位与曲线纵轴下限。</summary>
    public double ZMinLimit { get; init; }

    /// <summary>Z 轴行程上限（mm），示意图最高位与曲线纵轴上限。</summary>
    public double ZMaxLimit { get; init; } = 60.0;
}
