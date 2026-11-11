// 职责：操作模式枚举（自动 / 手动），与设备状态正交（05-核心状态机 第 1 节）
// 层：Core/StateMachine
// 依赖：无

namespace AutoTaping.Core.StateMachine;

/// <summary>
/// 操作模式。与 <see cref="DeviceState"/> 正交：运行中禁止切换模式（见 docs/05 第 1.1 节）。
/// </summary>
public enum OperationMode
{
    /// <summary>自动模式：可启动自动流程。</summary>
    Auto,

    /// <summary>手动模式：只能点动、单轴操作。</summary>
    Manual
}
