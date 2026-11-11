// 职责：视觉标定页的可配置参数（九点标定步长、预览刷新间隔、X/Y 软限位）
// 层：App/Infrastructure
// 依赖：无（由 App 启动时从 appsettings.json 填充后注册为单例）

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// 视觉页参数。来源：<c>appsettings.json</c> 的 <c>Vision</c> 段（步长、预览间隔）与
/// <c>Hardware:MotionCard:Axes</c>（X/Y 软限位，用于校验九点标定点在行程内）。
/// 这些是硬件/标定参数，不得写死在 ViewModel（10-编码约定 第八节）。
/// </summary>
public sealed class VisionOptions
{
    /// <summary>九点标定九宫格步长 L（mm），见 04 文档 2.2 节推荐 50mm。</summary>
    public double CalibrationStepMm { get; init; } = 50.0;

    /// <summary>实时预览刷新间隔（ms）。100ms 对应 10fps（11-开发任务分解 S5 验收标准 1）。</summary>
    public int PreviewIntervalMs { get; init; } = 100;

    /// <summary>相机软触发取图超时（ms）。</summary>
    public int CaptureTimeoutMs { get; init; } = 3000;

    /// <summary>预览初始曝光（微秒），默认 5000，来自 appsettings 的 Hardware:Camera:ExposureTime。</summary>
    public double DefaultExposureUs { get; init; } = 5000.0;

    /// <summary>预览初始增益（dB），默认 0，来自 appsettings 的 Hardware:Camera:Gain。</summary>
    public double DefaultGainDb { get; init; }

    /// <summary>各轴软限位下限，下标即 <see cref="AutoTaping.Core.Abstractions.AxisId"/>。</summary>
    public double[] MinLimits { get; init; } = { 0.0, 0.0, 0.0, -180.0 };

    /// <summary>各轴软限位上限，下标即 <see cref="AutoTaping.Core.Abstractions.AxisId"/>。</summary>
    public double[] MaxLimits { get; init; } = { 400.0, 400.0, 60.0, 180.0 };
}
