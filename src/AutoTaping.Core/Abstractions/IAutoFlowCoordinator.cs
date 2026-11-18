// 职责：自动流程协调器抽象——状态机 + 流程引擎 + 急停 + 操作条命令的统一入口，UI 只依赖本接口
// 层：Core/Abstractions
// 依赖：DeviceState、OperationMode（Core/StateMachine）

using AutoTaping.Core.Models;
using AutoTaping.Core.StateMachine;

namespace AutoTaping.Core.Abstractions;

/// <summary>
/// 自动流程协调器。持有 <see cref="DeviceStateMachine"/> 与流程引擎，对 UI 暴露启动 / 暂停 /
/// 停止 / 复位 / 急停等命令与状态 / 步骤事件；内部负责互锁检查、急停处理、运行时故障监视。
/// </summary>
public interface IAutoFlowCoordinator : IDisposable
{
    /// <summary>当前设备状态。</summary>
    DeviceState State { get; }

    /// <summary>当前操作模式。</summary>
    OperationMode Mode { get; }

    /// <summary>是否处于单步模式。</summary>
    bool IsSingleStep { get; }

    /// <summary>当前步骤序号（从 1 开始；未运行时为 0）。</summary>
    int CurrentStepIndex { get; }

    /// <summary>流程步骤总数。</summary>
    int TotalSteps { get; }

    /// <summary>当前步骤名（未运行时为 <see langword="null"/>）。</summary>
    string? CurrentStepName { get; }

    /// <summary>急停是否已被释放（急停释放后复位才会执行 ResetAxis）。</summary>
    bool IsEmergencyStopReleased { get; }

    /// <summary>
    /// 最近一次拍照识别结果（含 ΔX/ΔY/Δθ、MARK 分数与失败原因）；本次运行尚未拍过照为 <see langword="null"/>。
    /// 由流程线程整体替换不可变对象，界面直接读引用即可。
    /// </summary>
    VisionCorrection? LastVision { get; }

    /// <summary>
    /// 最近一帧视觉原始图（Mono8 副本 + 尺寸 + 版本号）；尚未拍过照为 <see langword="null"/>。
    /// 版本号变化时才需要重新转换显示，避免每帧重复转 130 万像素。
    /// </summary>
    VisionFrameSnapshot? LastFrame { get; }

    /// <summary>状态迁移成功时触发（订阅方在调用线程收到通知，不得做耗时操作）。</summary>
    event EventHandler? StateChanged;

    /// <summary>步骤推进或中断时触发（用于主画面高亮当前工位）。</summary>
    event EventHandler? StepChanged;

    /// <summary>完成启动自检，从 Initializing 迁到 NotReady / Alarming（硬件初始化后调用一次）。</summary>
    void CompleteSelfCheck();

    /// <summary>启动自动流程（Ready → Running）或从暂停恢复（Paused → Running）。</summary>
    void Start();

    /// <summary>暂停自动流程（Running → Paused），并立即停轴。</summary>
    void Pause();

    /// <summary>继续自动流程（Paused → Running）。</summary>
    void Resume();

    /// <summary>停止自动流程（Running / Paused → Ready）。</summary>
    void Stop();

    /// <summary>复位：按当前状态执行（Alarming 确认后复位 / EStopped 释放后 ResetAxis / NotReady 回零）。</summary>
    void Reset();

    /// <summary>急停（任意状态 → EStopped），执行 05 文档 5.2 节的固定顺序。</summary>
    void EmergencyStop();

    /// <summary>释放急停（虚拟模式 / 软急停按钮：置急停释放标记，供复位时 ResetAxis）。</summary>
    void ReleaseEmergencyStop();

    /// <summary>设置 / 取消单步模式。</summary>
    /// <param name="enabled">是否启用单步。</param>
    void SetSingleStep(bool enabled);

    /// <summary>切换操作模式（运行中禁止）。</summary>
    /// <param name="mode">目标模式。</param>
    bool TrySwitchMode(OperationMode mode);
}
