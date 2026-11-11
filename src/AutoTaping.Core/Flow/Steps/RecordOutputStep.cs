// 职责：步骤 15 计数 + 落库——写入产出记录，失败不影响生产只记日志（05 第 4.2 节第 15 步）
// 层：Core/Flow/Steps
// 依赖：IOutputRepository、IClock（Core/Abstractions）、OutputRecord（Core/Models）

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Core.Flow.Steps;

/// <summary>产出记录步骤（1s 超时）。写入失败只记日志不报警（05：记录失败不影响生产）。</summary>
public sealed class RecordOutputStep : FlowStep
{
    private readonly IOutputRepository _outputs;
    private readonly IClock _clock;
    private readonly ILogger<RecordOutputStep> _log;

    /// <summary>构造产出记录步骤。</summary>
    public RecordOutputStep(IOutputRepository outputs, IClock clock, ILogger<RecordOutputStep> log)
    {
        _outputs = outputs;
        _clock = clock;
        _log = log;
    }

    /// <inheritdoc />
    public override string Name => "RecordOutput";

    /// <inheritdoc />
    public override int TimeoutMs => 1_000;

    /// <inheritdoc />
    public override Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        var now = _clock.Now;
        var sequenceNo = _outputs.NextSequenceNo(now);
        var correction = ctx.LastCorrection;

        var record = new OutputRecord
        {
            SequenceNo = sequenceNo,
            RecipeId = ctx.Recipe.Id,
            RecipeName = ctx.Recipe.Name,
            Result = OutputRecord.ResultOk,
            CycleTimeMs = (int)ctx.StepWatch.ElapsedMilliseconds,
            DeltaX = correction?.DeltaX ?? 0.0,
            DeltaY = correction?.DeltaY ?? 0.0,
            DeltaTheta = correction?.DeltaTheta ?? 0.0,
            MatchScore1 = correction?.Score1 ?? 0.0,
            MatchScore2 = correction?.Score2 ?? 0.0,
            Shift = ShiftSchedule.GetShiftName(now),
            CreatedAt = now
        };

        try
        {
            _outputs.Insert(record);
            ctx.SequenceNo = sequenceNo + 1;
            _log.LogDebug("产出记录已写入：序号 {SequenceNo} 节拍 {CycleMs}ms ΔX={DeltaX:F3} ΔY={DeltaY:F3}",
                          sequenceNo, record.CycleTimeMs, record.DeltaX, record.DeltaY);
        }
        catch (Exception ex)
        {
            // 记录失败不影响生产，但要留日志（05 第 4.2 节第 15 步）
            _log.LogError(ex, "产出记录写入失败（不影响生产）：序号 {SequenceNo}", sequenceNo);
        }

        return Task.CompletedTask;
    }
}
