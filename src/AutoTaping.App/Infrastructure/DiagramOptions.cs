// 职责：主画面的坐标量程参数——4 轴软限位（曲线纵轴 + 四轴位置卡的行程条）
// 层：App/Infrastructure
// 依赖：AxisId（Core/Abstractions）；由 App 启动时从 appsettings.json 的 Hardware:MotionCard:Axes 填充后注册为单例

using AutoTaping.Core.Abstractions;

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// 主画面的坐标量程。来源：<c>appsettings.json</c> 的 <c>Hardware:MotionCard:Axes</c> 中各轴软限位
/// （10-编码约定 第八节：硬件参数进配置，不得写死在 ViewModel 里）。
/// </summary>
public sealed class DiagramOptions
{
    /// <summary>4 轴行程下限（mm，R 轴为 °），下标即 <see cref="AxisId"/>。</summary>
    public double[] MinLimits { get; init; } = new double[4];

    /// <summary>4 轴行程上限（mm，R 轴为 °），下标即 <see cref="AxisId"/>。</summary>
    public double[] MaxLimits { get; init; } = new double[4];
}
