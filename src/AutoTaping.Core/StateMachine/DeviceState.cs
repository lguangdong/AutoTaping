// 职责：设备状态枚举（状态灯、三色灯与互锁判定的唯一取值来源）
// 层：Core/StateMachine
// 依赖：无

namespace AutoTaping.Core.StateMachine;

/// <summary>
/// 设备状态。取值与 <c>docs/05-核心状态机与流程.md</c> 第 1.2 节完全一致，不得增删。
/// S3 只用它驱动状态栏状态灯（当前固定为 <see cref="NotReady"/>），
/// 状态迁移表与 <c>DeviceStateMachine</c> 在 S6 实现。
/// </summary>
public enum DeviceState
{
    /// <summary>启动自检中。</summary>
    Initializing,

    /// <summary>未就绪（未回零 / 相机未连接 / 气压低）。</summary>
    NotReady,

    /// <summary>就绪，等待启动。</summary>
    Ready,

    /// <summary>自动流程执行中。</summary>
    Running,

    /// <summary>暂停（可在当前步骤结束后恢复）。</summary>
    Paused,

    /// <summary>报警中（需确认 + 复位）。</summary>
    Alarming,

    /// <summary>急停中（需释放 + 复位 + 重新回零）。</summary>
    EStopped,

    /// <summary>正在退出。</summary>
    Exiting
}
