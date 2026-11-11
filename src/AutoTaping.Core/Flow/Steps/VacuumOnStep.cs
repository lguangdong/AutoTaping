// 职责：步骤 8 开真空并等待吸住——Y00 置位后轮询 X03，超时报警 AL301（05 第 4.2 节第 8 步）
// 层：Core/Flow/Steps
// 依赖：IIoService（Core/Abstractions）、IoPointNames / AlarmLevel（Core/Models）、FlowException

using System.Diagnostics;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Exceptions;
using AutoTaping.Core.Models;

namespace AutoTaping.Core.Flow.Steps;

/// <summary>开真空等待吸住步骤（2s 超时）。吸住确认 X03=1 后通过，否则报警 AL301。</summary>
public sealed class VacuumOnStep : FlowStep
{
    /// <summary>轮询 X03 的间隔（ms）。</summary>
    private const int PollIntervalMs = 50;

    private readonly IIoService _io;

    /// <summary>构造开真空步骤。</summary>
    public VacuumOnStep(IIoService io)
    {
        _io = io;
    }

    /// <inheritdoc />
    public override string Name => "VacuumOn";

    /// <inheritdoc />
    public override int TimeoutMs => 2_000;

    /// <inheritdoc />
    public override async Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        _io.WriteDo(IoPointNames.VacuumValve, true);

        var timeoutMs = ctx.Recipe.Parameters.Alarm.VacuumTimeoutMs;
        var stopwatch = Stopwatch.StartNew();

        while (!_io.ReadDi(IoPointNames.VacuumSensor))
        {
            ct.ThrowIfCancellationRequested();

            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
            {
                throw new FlowException(Name, "真空吸不住，请检查气路与真空泵",
                                        "AL301", AlarmLevel.Critical, "贴附头",
                                        pauseInsteadOfAlarm: false, innerException: null);
            }

            await Task.Delay(PollIntervalMs, ct);
        }
    }
}
