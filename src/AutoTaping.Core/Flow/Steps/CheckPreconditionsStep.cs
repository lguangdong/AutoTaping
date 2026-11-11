// 职责：步骤 1 前置检查——再次校验互锁（回零/安全门/气压/相机/运控卡/配方/急停/报警），任一不满足则中止
// 层：Core/Flow/Steps
// 依赖：InterlockChecker、IDeviceMonitor、IAlarmService（Core/Abstractions）、FlowException

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Exceptions;
using AutoTaping.Core.Models;
using AutoTaping.Core.Services;

namespace AutoTaping.Core.Flow.Steps;

/// <summary>前置检查步骤（05 第 4.2 节第 1 步，1s 超时，不重试）。防御性再校验，避免带着隐患进入运动。</summary>
public sealed class CheckPreconditionsStep : FlowStep
{
    private readonly InterlockChecker _interlock;
    private readonly IDeviceMonitor _monitor;
    private readonly IAlarmService _alarm;

    /// <summary>构造前置检查步骤。</summary>
    public CheckPreconditionsStep(InterlockChecker interlock, IDeviceMonitor monitor, IAlarmService alarm)
    {
        _interlock = interlock;
        _monitor = monitor;
        _alarm = alarm;
    }

    /// <inheritdoc />
    public override string Name => "CheckPreconditions";

    /// <inheritdoc />
    public override int TimeoutMs => 1_000;

    /// <inheritdoc />
    public override Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        var errors = _interlock.CheckForStart(_monitor.Current, ctx.Recipe, _alarm.HasUnackedCritical());
        if (errors.Count > 0)
        {
            throw new FlowException(Name, "前置检查失败：" + string.Join("；", errors),
                                    alarmCode: null, alarmLevel: AlarmLevel.Critical,
                                    alarmSource: null, pauseInsteadOfAlarm: false, innerException: null);
        }

        return Task.CompletedTask;
    }
}
