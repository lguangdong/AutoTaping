// 职责：步骤 6 取料——Z 下降到料盘面、开真空吸住一片胶带、确认 X03 后抬起（05 第 4.2 节第 6 步）
// 层：Core/Flow/Steps
// 依赖：IMotionCard、IIoService（Core/Abstractions）、IoPointNames / AlarmLevel、FlowException

using System.Diagnostics;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Exceptions;
using AutoTaping.Core.Models;

namespace AutoTaping.Core.Flow.Steps;

/// <summary>
/// 取料步骤（3s 超时）。Z 下降到料盘面 → 开真空等待吸住 → Z 抬回安全高度。
/// <para>
/// 点贴相对滚贴省掉了卷料、剥离刀、收膜轮与张力控制，取料只需"下降—吸住—抬起"三个动作。
/// </para>
/// </summary>
public sealed class PickTapeStep : FlowStep
{
    /// <summary>轮询 X03 的间隔（ms）。</summary>
    private const int PollIntervalMs = 50;

    private readonly IMotionCard _motion;
    private readonly IIoService _io;

    /// <summary>构造取料步骤。</summary>
    /// <param name="motion">运控卡。</param>
    /// <param name="io">IO 服务。</param>
    public PickTapeStep(IMotionCard motion, IIoService io)
    {
        _motion = motion;
        _io = io;
    }

    /// <inheritdoc />
    public override string Name => "PickTape";

    /// <summary>Z 下降 + 真空等待 + Z 抬起，三段合计约 3.5s，留 6s 余量。</summary>
    public override int TimeoutMs => 6_000;

    /// <inheritdoc />
    public override async Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        var process = ctx.Recipe.Parameters.Process;
        var speed = process.MoveSpeedMmPerSec;

        // 1. 下降到取料高度（料盘面与工件面接近，复用贴附高度参数）
        await MoveHelper.MoveAsync(_motion, AxisId.Z, process.TapeHeightMm,
                                   speed, TimeoutMs, Name, ct);

        // 2. 开真空并等待吸住
        _io.WriteDo(IoPointNames.VacuumValve, true);

        var timeoutMs = ctx.Recipe.Parameters.Alarm.VacuumTimeoutMs;
        var stopwatch = Stopwatch.StartNew();

        while (!_io.ReadDi(IoPointNames.VacuumSensor))
        {
            ct.ThrowIfCancellationRequested();

            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
            {
                throw new FlowException(Name, "取料真空吸不住，请检查料盘是否有料、吸嘴是否堵塞",
                                        "AL301", AlarmLevel.Critical, "贴附头",
                                        pauseInsteadOfAlarm: false, innerException: null);
            }

            await Task.Delay(PollIntervalMs, ct);
        }

        // 3. 抬起，准备去贴附位
        await MoveHelper.MoveAsync(_motion, AxisId.Z, process.SafeHeightMm,
                                   speed, TimeoutMs, Name, ct);
    }
}
