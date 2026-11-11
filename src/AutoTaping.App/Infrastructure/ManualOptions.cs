// 职责：手动调试页的可配置参数（点动默认速度、速度上下限、Z 轴安全高度、4 轴软限位）
// 层：App/Infrastructure
// 依赖：无（由 App 启动时从 appsettings.json 填充后注册为单例）

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// 手动页参数。来源：<c>appsettings.json</c> 的 <c>Manual</c> 段（速度）、
/// <c>Process:SafeHeight</c>（Z 轴安全高度）与 <c>Hardware:MotionCard:Axes</c>（软限位）。
/// 这些是硬件/安全参数，不得写死在 ViewModel 里（10-编码约定 第八节）。
/// </summary>
public sealed class ManualOptions
{
    /// <summary>点动与页面内定位的默认速度（mm/s，R 轴为 °/s）。08-页面清单 页面 2 的示例值 20。</summary>
    public double DefaultSpeed { get; init; } = 20.0;

    /// <summary>手动速度下限（mm/s）。低于该值电机几乎不动，现场会误判为故障。</summary>
    public double MinSpeed { get; init; } = 1.0;

    /// <summary>手动速度上限（mm/s）。手动调试必须限速，防止误操作撞机。</summary>
    public double MaxSpeed { get; init; } = 100.0;

    /// <summary>Z 轴安全高度（mm）。离开手动页时抬到此高度（08-页面清单 页面 2 安全要求 3）。</summary>
    public double SafeHeightMm { get; init; } = 50.0;

    /// <summary>各轴软限位下限，下标即 AxisId。</summary>
    public double[] MinLimits { get; init; } = { 0.0, 0.0, 0.0, -180.0 };

    /// <summary>各轴软限位上限，下标即 AxisId。</summary>
    public double[] MaxLimits { get; init; } = { 400.0, 400.0, 60.0, 180.0 };
}
