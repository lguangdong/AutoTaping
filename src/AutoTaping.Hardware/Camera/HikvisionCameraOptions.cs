// 职责：海康相机初始化所需的硬件参数模型，由 App 从 appsettings.json 的 Hardware:Camera 段填充
// 层：Hardware/Camera
// 依赖：无

namespace AutoTaping.Hardware.Camera;

/// <summary>
/// 海康相机参数（03 文档第 5 节）。来源：<c>appsettings.json</c> 的 <c>Hardware:Camera</c> 段，
/// 硬件参数不得写死（10-编码约定 第八节）。自动曝光/自动增益在代码中强制关闭，不由配置决定（03 文档第 8 节）。
/// </summary>
public sealed class HikvisionCameraOptions
{
    /// <summary>相机序列号；空串表示取枚举到的第一台（单相机场景）。</summary>
    public string SerialNumber { get; init; } = "";

    /// <summary>曝光时间（μs），默认 5000。</summary>
    public double ExposureTimeUs { get; init; } = 5000.0;

    /// <summary>增益（dB），默认 0（优先调曝光而非增益）。</summary>
    public double GainDb { get; init; } = 0.0;

    /// <summary>触发模式，默认 Software（软触发）。</summary>
    public string TriggerMode { get; init; } = "Software";

    /// <summary>取图超时（ms），默认 3000。</summary>
    public int TimeoutMs { get; init; } = 3000;
}
