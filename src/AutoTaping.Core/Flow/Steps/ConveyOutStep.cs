// 职责：步骤 12 传送带放行——开带即返回，停线交给下一拍 WaitCarrier 检测到料后完成（05 第 4.2 节第 12 步）
// 层：Core/Flow/Steps
// 依赖：IIoService（Core/Abstractions）、IoPointNames

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;

namespace AutoTaping.Core.Flow.Steps;

/// <summary>
/// 传送带放行步骤。只负责开带，**不做定时关门**——载具驶出工位后由皮带连续输送给下一件，
/// 下一拍 <see cref="WaitCarrierStep"/> 检测到 X00 再关带，从而消除「放行—等料」之间的空转停顿。
/// <para>
/// 停位精度仍靠「停线 + 视觉纠偏」保证；出料侧堵料由 X01 在下一拍入口判断。
/// 非正常退出（暂停 / 停止 / 急停 / 流程异常暂停）由 <c>AutoFlowCoordinator</c> 负责关带。
/// </para>
/// </summary>
public sealed class ConveyOutStep : FlowStep
{
    private readonly IIoService _io;

    /// <summary>构造传送带放行步骤。</summary>
    /// <param name="io">IO 服务。</param>
    public ConveyOutStep(IIoService io)
    {
        _io = io;
    }

    /// <inheritdoc />
    public override string Name => "ConveyOut";

    /// <summary>开带是瞬时动作，5s 仅作引擎兜底。</summary>
    public override int TimeoutMs => 5_000;

    /// <inheritdoc />
    public override Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        _io.WriteDo(IoPointNames.ConveyorRun, true);
        return Task.CompletedTask;
    }
}
