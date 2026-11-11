// 职责：步骤 12 关真空 + 吹气破真空——Y00 关、Y01 开并延时（05 第 4.2 节第 12 步）
// 层：Core/Flow/Steps
// 依赖：IIoService（Core/Abstractions）、IoPointNames（Core/Models）

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;

namespace AutoTaping.Core.Flow.Steps;

/// <summary>关真空 + 吹气步骤（1s 超时）。</summary>
public sealed class VacuumOffAndBlowStep : FlowStep
{
    private readonly IIoService _io;

    /// <summary>构造关真空吹气步骤。</summary>
    public VacuumOffAndBlowStep(IIoService io)
    {
        _io = io;
    }

    /// <inheritdoc />
    public override string Name => "VacuumOffAndBlow";

    /// <inheritdoc />
    public override int TimeoutMs => 1_000;

    /// <inheritdoc />
    public override async Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        _io.WriteDo(IoPointNames.VacuumValve, false);
        _io.WriteDo(IoPointNames.BlowValve, true);

        await Task.Delay(ctx.Recipe.Parameters.Process.BlowOffMs, ct);

        _io.WriteDo(IoPointNames.BlowValve, false);
    }
}
