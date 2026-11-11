// 职责：工业相机抽象——软触发取图、曝光/增益控制、掉线事件（签名与 docs/03 第 1 节完全一致，不得私自变更）
// 层：Core/Abstractions
// 依赖：无（纯接口与数据类）

namespace AutoTaping.Core.Abstractions;

/// <summary>一帧取图结果。</summary>
public sealed class CaptureResult
{
    /// <summary>是否成功取到有效帧。</summary>
    public bool Success { get; init; }

    /// <summary>灰度原始数据（Mono8，一像素一字节）；失败时为 <see langword="null"/>。</summary>
    public byte[]? Mono8Data { get; init; }

    /// <summary>图像宽度（像素）。</summary>
    public int Width { get; init; }

    /// <summary>图像高度（像素）。</summary>
    public int Height { get; init; }

    /// <summary>取图时间戳。</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>失败原因（面向操作工的可读描述）；成功时为 <see langword="null"/>。</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// 工业相机抽象。取图不得在 UI 线程调用，且不可多线程并发取图（由调用方串行化）。
/// 真实实现见 <c>AutoTaping.Hardware.Camera.HikvisionCamera</c>（S8 接入），
/// 无硬件时见 <c>AutoTaping.Hardware.Camera.VirtualCamera</c>。
/// </summary>
public interface ICamera : IDisposable
{
    /// <summary>初始化相机。返回 <see langword="false"/> 表示失败（调用方降级为 Virtual）。</summary>
    bool Initialize();

    /// <summary>相机是否已连接。</summary>
    bool IsConnected { get; }

    /// <summary>是否处于采集状态。</summary>
    bool IsGrabbing { get; }

    /// <summary>开始采集（连续采集模式，取图靠 <see cref="TriggerAndGrabAsync"/>）。</summary>
    bool StartGrabbing();

    /// <summary>停止采集。</summary>
    void StopGrabbing();

    /// <summary>软触发一次并取一帧。超时返回 Success=false。</summary>
    Task<CaptureResult> TriggerAndGrabAsync(int timeoutMs, CancellationToken ct);

    /// <summary>仅软触发（用于已配回调模式的场景）。</summary>
    void SoftwareTrigger();

    /// <summary>设置曝光（微秒）。</summary>
    bool SetExposure(double microseconds);

    /// <summary>设置增益（dB）。</summary>
    bool SetGain(double gain);

    /// <summary>相机异常（掉线）。</summary>
    event EventHandler<CameraFaultEventArgs>? Fault;
}

/// <summary>相机异常事件参数。</summary>
public sealed class CameraFaultEventArgs : EventArgs
{
    /// <summary>异常描述。</summary>
    public string Message { get; init; } = "";

    /// <summary>SDK 错误码。</summary>
    public int ErrorCode { get; init; }
}
