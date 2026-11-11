// 职责：步骤 10 传送带放行——Y07=1 跑一段固定时间后停线，把载具送出工位（05 第 4.2 节第 10 步）
// 层：Core/Flow/Steps
// 依赖：IIoService（Core/Abstractions）、IoPointNames

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;

namespace AutoTaping.Core.Flow.Steps;

/// <summary>
/// 传送带放行步骤（5s 超时）。开皮带跑固定时间即停，不需要出料传感器参与闭环 ——
/// 停位精度靠"停线 + 视觉纠偏"保证，出料是否堵塞由 X01（出料侧有料）在下一拍入口判断。
/// </summary>
public sealed class ConveyOutStep : FlowStep
{
    /// <summary>
    /// 放行时长（ms）。按"载具间距 170mm ÷ 皮带速度约 200mm/s"估算，
    /// 保证当前载具驶出工位即可，不必等它走完全程。
    /// </summary>
    private const int ReleaseMs = 900;

    private readonly IIoService _io;

    /// <summary>构造放行步骤。</summary>
    /// <param name="io">IO 服务。</param>
    public ConveyOutStep(IIoService io)
    {
        _io = io;
    }

    /// <inheritdoc />
    public override string Name => "ConveyOut";

    /// <inheritdoc />
    public override int TimeoutMs => 5_000;

    /// <inheritdoc />
    public override async Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        _io.WriteDo(IoPointNames.ConveyorRun, true);
        await Task.Delay(ReleaseMs, ct);
        _io.WriteDo(IoPointNames.ConveyorRun, false);
    }
}
