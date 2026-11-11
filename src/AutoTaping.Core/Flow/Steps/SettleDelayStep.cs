// 职责：步骤 4 稳定延时——拍照前等待机械振动消除（05 第 4.2 节第 4 步，300ms）
// 层：Core/Flow/Steps
// 依赖：无

namespace AutoTaping.Core.Flow.Steps;

/// <summary>稳定延时步骤（300ms，无失败）。</summary>
public sealed class SettleDelayStep : FlowStep
{
    /// <summary>稳定延时（ms），消除机械振动（04 文档 2.2 节第 3 步）。</summary>
    private const int SettleDelayMs = 300;

    /// <inheritdoc />
    public override string Name => "SettleDelay";

    /// <inheritdoc />
    public override int TimeoutMs => 1_000;

    /// <inheritdoc />
    public override async Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        await Task.Delay(SettleDelayMs, ct);
    }
}
