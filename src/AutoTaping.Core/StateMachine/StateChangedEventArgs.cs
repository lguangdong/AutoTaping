// 职责：状态迁移事件参数（旧状态 → 新状态）
// 层：Core/StateMachine
// 依赖：无

namespace AutoTaping.Core.StateMachine;

/// <summary>设备状态迁移事件参数。</summary>
public sealed class StateChangedEventArgs : EventArgs
{
    /// <summary>迁移前的状态。</summary>
    public DeviceState From { get; }

    /// <summary>迁移后的状态。</summary>
    public DeviceState To { get; }

    /// <summary>构造迁移事件参数。</summary>
    /// <param name="from">旧状态。</param>
    /// <param name="to">新状态。</param>
    public StateChangedEventArgs(DeviceState from, DeviceState to)
    {
        From = from;
        To = to;
    }
}
