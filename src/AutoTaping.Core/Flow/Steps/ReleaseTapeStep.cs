// 职责：步骤 9 卸料——吹气破真空让胶带留在工件上，压合抬起、Z 回安全高度（05 第 4.2 节第 9 步）
// 层：Core/Flow/Steps
// 依赖：IMotionCard、IIoService（Core/Abstractions）、IoPointNames

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;

namespace AutoTaping.Core.Flow.Steps;

/// <summary>
/// 卸料步骤（3s 超时）。**必须先吹气让胶带脱离吸嘴，再抬 Z**，否则胶带会被吸嘴带回，
/// 下一件就取不到料（现场最常见的一类"贴不住"故障）。
/// </summary>
public sealed class ReleaseTapeStep : FlowStep
{
    private readonly IMotionCard _motion;
    private readonly IIoService _io;

    /// <summary>构造卸料步骤。</summary>
    /// <param name="motion">运控卡。</param>
    /// <param name="io">IO 服务。</param>
    public ReleaseTapeStep(IMotionCard motion, IIoService io)
    {
        _motion = motion;
        _io = io;
    }

    /// <inheritdoc />
    public override string Name => "ReleaseTape";

    /// <summary>吹气（最大 1000ms）+ Z 抬起，留余量。</summary>
    public override int TimeoutMs => 3_000;

    /// <inheritdoc />
    public override async Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        var process = ctx.Recipe.Parameters.Process;

        // 1. 关真空、开吹气：胶带离开吸嘴落到工件上
        _io.WriteDo(IoPointNames.VacuumValve, false);
        _io.WriteDo(IoPointNames.BlowValve, true);
        await Task.Delay(process.BlowOffMs, ct);

        // 2. 关吹气、压合抬起
        _io.WriteDo(IoPointNames.BlowValve, false);
        _io.WriteDo(IoPointNames.PressCylinder, false);

        // 3. Z 回安全高度，让出视野给下一件
        await MoveHelper.MoveAsync(_motion, AxisId.Z, process.SafeHeightMm,
                                   process.MoveSpeedMmPerSec, TimeoutMs, Name, ct);
    }
}
