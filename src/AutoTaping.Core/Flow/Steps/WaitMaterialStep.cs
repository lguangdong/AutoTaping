// 职责：步骤 2 等待上料位有料——X00=1 才继续，超时 30s 只暂停不报警；同时承担节拍起点的配方参数热刷新
// 层：Core/Flow/Steps
// 依赖：IIoService、IRecipeRepository（Core/Abstractions）、IoPointNames / AlarmLevel（Core/Models）、FlowException、ILogger

using System.Diagnostics;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Exceptions;
using AutoTaping.Core.Models;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Core.Flow.Steps;

/// <summary>
/// 等待上料步骤（05 第 4.2 节第 2 步）。检测到有料时开始单件节拍计时（05 第 7.1 节）。
/// <para>
/// 参数热生效（09 文档第四节）：本步骤是引擎每个节拍的起点（引擎走完第 15 步后回到本步），
/// 因此在等待上料前先用「当前激活配方」的最新参数刷新流程上下文，
/// 使改完参数在下一个节拍生效而无须重启软件。
/// </para>
/// </summary>
public sealed class WaitMaterialStep : FlowStep
{
    /// <summary>上料等待超时（ms），超时报警 AL403（提示级）并暂停。</summary>
    private const int MaterialTimeoutMs = 30_000;

    /// <summary>轮询 X00 的间隔（ms）。</summary>
    private const int PollIntervalMs = 100;

    private readonly IIoService _io;
    private readonly IRecipeRepository _recipes;
    private readonly ILogger<WaitMaterialStep> _log;

    /// <summary>构造等待上料步骤。</summary>
    /// <param name="io">IO 服务。</param>
    /// <param name="recipes">配方仓储（每个节拍起点刷新参数，实现参数热生效）。</param>
    /// <param name="log">日志。</param>
    public WaitMaterialStep(IIoService io, IRecipeRepository recipes, ILogger<WaitMaterialStep> log)
    {
        _io = io;
        _recipes = recipes;
        _log = log;
    }

    /// <inheritdoc />
    public override string Name => "WaitMaterial";

    /// <summary>引擎兜底超时略大于内部 30s 判定，避免抢在"暂停"语义之前。</summary>
    public override int TimeoutMs => MaterialTimeoutMs + 2_000;

    /// <inheritdoc />
    public override async Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        RefreshRecipeParameters(ctx);

        var stopwatch = Stopwatch.StartNew();

        while (!_io.ReadDi(IoPointNames.MaterialAtLoad))
        {
            ct.ThrowIfCancellationRequested();

            if (stopwatch.ElapsedMilliseconds >= MaterialTimeoutMs)
            {
                throw new FlowException(Name, "上料位长时间无料，请补充物料",
                                        "AL403", AlarmLevel.Info, "上料",
                                        pauseInsteadOfAlarm: true, innerException: null);
            }

            await Task.Delay(PollIntervalMs, ct);
        }

        // 检测到有料 → 开始单件节拍计时
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
