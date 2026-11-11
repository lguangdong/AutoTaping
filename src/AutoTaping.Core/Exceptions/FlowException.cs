// 职责：自动流程步骤失败异常——带步骤名、可选报警码与"暂停而非报警"标记
// 层：Core/Exceptions
// 依赖：AlarmLevel（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.Core.Exceptions;

/// <summary>
/// 流程步骤执行失败（10-编码约定 6.1）。步骤内部抛出后，由流程协调器统一处理：
/// 携带报警码时先按码报警再转 <see cref="AutoTaping.Core.StateMachine.DeviceState.Alarming"/>，
/// <see cref="PauseInsteadOfAlarm"/> 为 <see langword="true"/> 时只暂停（如待料超时），不报警。
/// </summary>
public sealed class FlowException : Exception
{
    /// <summary>失败的步骤名（如 <c>CaptureAndCorrect</c>）。</summary>
    public string StepName { get; }

    /// <summary>对应的报警码（见 <c>docs/09-报警与参数清单.md</c>）；无特定报警时为 <see langword="null"/>。</summary>
    public string? AlarmCode { get; }

    /// <summary>报警级别，默认严重。</summary>
    public AlarmLevel AlarmLevel { get; }

    /// <summary>报警来源（如 <c>视觉</c> / <c>贴附头</c>）。</summary>
    public string? AlarmSource { get; }

    /// <summary>为 <see langword="true"/> 时只暂停不报警（待料超时等非故障场景）。</summary>
    public bool PauseInsteadOfAlarm { get; }

    /// <summary>构造无报警信息的流程异常（供引擎在重试耗尽后统一抛出）。</summary>
    /// <param name="message">面向操作工的可读原因。</param>
    public FlowException(string message)
        : this("", message, null, AlarmLevel.Critical, null, false, null)
    {
    }

    /// <summary>构造带步骤名与报警信息的流程异常。</summary>
    /// <param name="stepName">步骤名。</param>
    /// <param name="message">面向操作工的可读原因。</param>
    /// <param name="alarmCode">报警码，无则 <see langword="null"/>。</param>
    /// <param name="alarmLevel">报警级别。</param>
    /// <param name="alarmSource">报警来源。</param>
    /// <param name="pauseInsteadOfAlarm">是否只暂停不报警。</param>
    /// <param name="innerException">内部异常。</param>
    public FlowException(string stepName, string message, string? alarmCode,
                         AlarmLevel alarmLevel, string? alarmSource,
                         bool pauseInsteadOfAlarm, Exception? innerException)
        : base(message, innerException)
    {
        StepName = stepName;
        AlarmCode = alarmCode;
        AlarmLevel = alarmLevel;
        AlarmSource = alarmSource;
        PauseInsteadOfAlarm = pauseInsteadOfAlarm;
    }
}
