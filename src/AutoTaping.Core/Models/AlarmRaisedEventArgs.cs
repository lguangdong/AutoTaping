// 职责：报警触发事件参数（新报警落库后通知界面刷新与蜂鸣），含去重窗口内的重复次数
// 层：Core/Models
// 依赖：AlarmRecord

namespace AutoTaping.Core.Models;

/// <summary>
/// 报警触发事件参数。订阅方在触发线程上收到通知，必须自行切回 UI 线程再改界面
/// （01-架构设计 第 3 节）。
/// </summary>
public sealed class AlarmRaisedEventArgs : EventArgs
{
    /// <summary>构造事件参数。</summary>
    /// <param name="record">本次报警记录（已落库，带主键）。</param>
    /// <param name="repeatCount">5 秒去重窗口内的累计触发次数（首次为 1）。</param>
    public AlarmRaisedEventArgs(AlarmRecord record, int repeatCount)
    {
        ArgumentNullException.ThrowIfNull(record);
        Record = record;
        RepeatCount = repeatCount;
    }

    /// <summary>报警记录。</summary>
    public AlarmRecord Record { get; }

    /// <summary>5 秒去重窗口内的累计触发次数（首次为 1）。</summary>
    public int RepeatCount { get; }
}
