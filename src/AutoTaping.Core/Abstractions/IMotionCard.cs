// 职责：运动控制卡抽象——4 轴运动、回零、软限位、急停的统一接口（签名与 docs/02 第 1 节完全一致，不得私自变更）
// 层：Core/Abstractions
// 依赖：无（纯接口与事件参数）

namespace AutoTaping.Core.Abstractions;

/// <summary>轴标识。取值与运控卡通道号一一对应，业务层不得自行映射到别的轴号。</summary>
public enum AxisId { X = 0, Y = 1, Z = 2, R = 3 }

/// <summary>
/// 运动控制卡抽象。所有方法必须线程安全（真实卡的 DLL 非线程安全，由实现内部加锁）。
/// 真实实现见 <c>AutoTaping.Hardware.Motion.LeadshineMotionCard</c>（S8 接入），
/// 无硬件时见 <c>AutoTaping.Hardware.Motion.VirtualMotionCard</c>。
/// </summary>
public interface IMotionCard : IDisposable
{
    /// <summary>初始化板卡。返回 <see langword="false"/> 表示失败（调用方降级为 Virtual）。</summary>
    bool Initialize();

    /// <summary>板卡是否已连接。</summary>
    bool IsConnected { get; }

    // ---------- 状态 ----------
    /// <summary>读取轴当前位置（已换算为 mm 或 deg）。</summary>
    double GetPosition(AxisId axis);

    /// <summary>轴是否到位（<see langword="true"/> = 已停止且到位）。</summary>
    bool IsInPosition(AxisId axis);

    /// <summary>读取停止原因。正常=0，限位=1，急停=2，软限位=3，驱动报警=4。</summary>
    int GetStopReason(AxisId axis);

    // ---------- 回零 ----------
    /// <summary>回零（不阻塞，需轮询 <see cref="IsHomeCompleted"/>）。</summary>
    void Home(AxisId axis);

    /// <summary>回零是否完成。</summary>
    bool IsHomeCompleted(AxisId axis);

    // ---------- 运动 ----------
    /// <summary>绝对定位（不阻塞）。速度为 mm/s（R 轴为 °/s）。</summary>
    void MoveAbsolute(AxisId axis, double position, double speed);

    /// <summary>相对移动（不阻塞）。</summary>
    void MoveRelative(AxisId axis, double distance, double speed);

    /// <summary>点动：持续运动，调用 <see cref="Stop"/> 才停。dir = 1 正 / -1 负。</summary>
    void JogStart(AxisId axis, int direction, double speed);

    /// <summary>停止。emergency=false 减速停，true 急停。</summary>
    void Stop(AxisId axis, bool emergency);

    /// <summary>全轴急停。</summary>
    void EmergencyStopAll();

    /// <summary>清轴状态（急停解除后必须调用才能再动）。</summary>
    void ResetAxis(AxisId axis);

    /// <summary>软限位设置。</summary>
    void SetSoftLimit(AxisId axis, bool enable, double min, double max);

    /// <summary>
    /// 按当前速度曲线移动到一点并等待到位（阻塞，带超时）。
    /// 供流程引擎使用；timeoutMs 超时返回 <see langword="false"/>。
    /// </summary>
    Task<bool> MoveAndWaitAsync(AxisId axis, double position, double speed, int timeoutMs, CancellationToken ct);

    // ---------- 事件 ----------
    /// <summary>轴异常（限位、驱动报警），由采集线程触发。</summary>
    event EventHandler<AxisFaultEventArgs>? AxisFault;
}

/// <summary>轴异常事件参数。</summary>
public sealed class AxisFaultEventArgs : EventArgs
{
    /// <summary>发生异常的轴。</summary>
    public AxisId Axis { get; init; }

    /// <summary>停止原因，取值同 <see cref="IMotionCard.GetStopReason"/>。</summary>
    public int Reason { get; init; }

    /// <summary>面向操作工的可读描述。</summary>
    public string Message { get; init; } = "";
}
