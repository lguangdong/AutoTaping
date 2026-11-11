// 职责：步骤 8 压贴——Z 降到贴附高度、压合气缸下压、保压（05 第 4.2 节第 8 步）
// 层：Core/Flow/Steps
// 依赖：IMotionCard、IIoService（Core/Abstractions）、IoPointNames

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;

namespace AutoTaping.Core.Flow.Steps;

/// <summary>
/// 压贴并保压步骤（5s 超时）。点贴没有贴附路径，Z 到位后压合气缸一次下压，保压后即完成贴附。
/// </summary>
public sealed class PressAndHoldStep : FlowStep
{
    private readonly IMotionCard _motion;
    private readonly IIoService _io;

    /// <summary>构造压贴步骤。</summary>
    /// <param name="motion">运控卡。</param>
    /// <param name="io">IO 服务。</param>
    public PressAndHoldStep(IMotionCard motion, IIoService io)
    {
        _motion = motion;
        _io = io;
    }

    /// <inheritdoc />
    public override string Name => "PressAndHold";

    /// <summary>Z 下降 + 保压（最大 3000ms），留余量。</summary>
    public override int TimeoutMs => 5_000;

    /// <inheritdoc />
    public override async Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        var process = ctx.Recipe.Parameters.Process;

        await MoveHelper.MoveAsync(_motion, AxisId.Z, process.TapeHeightMm,
                                   process.MoveSpeedMmPerSec, TimeoutMs, Name, ct);

        _io.WriteDo(IoPointNames.PressCylinder, true);
        await Task.Delay(process.HoldPressureMs, ct);
    }
}
