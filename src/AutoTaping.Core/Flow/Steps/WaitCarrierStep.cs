// 职责：步骤 2 等待载具到位并停线——DI X00=1 后立即停线（Y07=0）；超时 30s 只暂停不报警
// 层：Core/Flow/Steps
// 依赖：IIoService、IRecipeRepository（Core/Abstractions）、IoPointNames / AlarmLevel、FlowException、ILogger

using System.Diagnostics;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Exceptions;
using AutoTaping.Core.Models;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Core.Flow.Steps;

/// <summary>
/// 等待载具到位步骤（05 第 4.2 节第 2 步）。产线形态下由光电检测载具，到位即停线，
/// 靠皮带摩擦把载具停在工位；超时只暂停（可能是上游没料），不报警停机。
/// <para>
/// 本步骤同时承担节拍起点的配方参数热刷新（09 文档第四节）：改完参数在下一个节拍生效。
/// </para>
/// </summary>
public sealed class WaitCarrierStep : FlowStep
{
    /// <summary>载具等待超时（ms），超时报警 AL403（提示级）并暂停。</summary>
    private const int CarrierTimeoutMs = 30_000;

    /// <summary>轮询 X00 的间隔（ms）。</summary>
    private const int PollIntervalMs = 100;

    private readonly IIoService _io;
    private readonly IRecipeRepository _recipes;
    private readonly ILogger<WaitCarrierStep> _log;

    /// <summary>构造等待载具步骤。</summary>
    /// <param name="io">IO 服务。</param>
    /// <param name="recipes">配方仓储（每个节拍起点刷新参数，实现参数热生效）。</param>
    /// <param name="log">日志。</param>
    public WaitCarrierStep(IIoService io, IRecipeRepository recipes, ILogger<WaitCarrierStep> log)
    {
        _io = io;
        _recipes = recipes;
        _log = log;
    }

    /// <inheritdoc />
    public override string Name => "WaitCarrier";

    /// <summary>引擎兜底超时略大于内部 30s 判定，避免抢在"暂停"语义之前。</summary>
    public override int TimeoutMs => CarrierTimeoutMs + 2_000;

    /// <inheritdoc />
    public override async Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        RefreshRecipeParameters(ctx);

        // 传送带先跑起来等来料；若已在上游运行则保持
        _io.WriteDo(IoPointNames.ConveyorRun, true);

        var stopwatch = Stopwatch.StartNew();

        while (!_io.ReadDi(IoPointNames.MaterialAtLoad))
        {
            ct.ThrowIfCancellationRequested();

            if (stopwatch.ElapsedMilliseconds >= CarrierTimeoutMs)
            {
                throw new FlowException(Name, "载具长时间未到位，请检查上游来料",
                                        "AL403", AlarmLevel.Info, "上料",
                                        pauseInsteadOfAlarm: true, innerException: null);
            }

            await Task.Delay(PollIntervalMs, ct);
        }

        // 到位立即停线：停位精度靠皮带摩擦 + 后续视觉纠偏
        _io.WriteDo(IoPointNames.ConveyorRun, false);
        _log.LogDebug("载具到位，停线");

        // 检测到载具 → 开始单件节拍计时
        ctx.StepWatch.Restart();
    }

    /// <summary>
    /// 用激活配方的最新参数刷新流程上下文。只替换 <c>Parameters</c> 引用（步骤只读该对象），
    /// 不改 <c>ctx.Recipe</c> 实例，避免与正在执行的步骤竞争。
    /// </summary>
    private void RefreshRecipeParameters(FlowContext ctx)
    {
        try
        {
            var active = _recipes.GetActive();
            if (active is null)
            {
                // 配方被删除时沿用本拍已有参数，由互锁在下一拍启动时拦截（05 文档第 6 节第 6 条）
                _log.LogWarning("节拍开始未取到激活配方，本拍沿用上一拍参数");
                return;
            }

            ctx.Recipe.Parameters = active.Parameters;
        }
        catch (Exception ex)
        {
            // 读库失败不得中断生产：本拍沿用上一拍参数，下拍再试
            _log.LogWarning(ex, "节拍开始刷新配方参数失败，本拍沿用上一拍参数");
        }
    }
}
