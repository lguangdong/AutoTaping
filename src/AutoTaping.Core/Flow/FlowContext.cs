// 职责：流程上下文——单件生产过程中的共享状态（配方、当前纠偏、流水号、节拍计时）
// 层：Core/Flow
// 依赖：Recipe、VisionCorrection（Core/Models）

using System.Diagnostics;
using AutoTaping.Core.Models;

namespace AutoTaping.Core.Flow;

/// <summary>
/// 自动流程执行上下文。一次连续生产共享同一实例，步骤间通过它传递纠偏结果与节拍计时
/// （见 <c>docs/05-核心状态机与流程.md</c> 第 4.1 节）。
/// </summary>
public sealed class FlowContext
{
    /// <summary>当前使用的配方（流程启动时确定，运行中不变）。</summary>
    public Recipe Recipe { get; init; } = null!;

    /// <summary>最近一次拍照得到的纠偏结果；拍照失败时为 <see langword="null"/>。</summary>
    public VisionCorrection? LastCorrection { get; set; }

    /// <summary>贴附基准叠加纠偏与旋转中心补偿后的有效 X（mm），由 MoveToTapePos 计算、后续贴附步骤使用。</summary>
    public double CorrectedBaseX { get; set; }

    /// <summary>贴附基准叠加纠偏与旋转中心补偿后的有效 Y（mm）。</summary>
    public double CorrectedBaseY { get; set; }

    /// <summary>当日流水号（RecordOutput 落库后自增）。</summary>
    public int SequenceNo { get; set; } = 1;

    /// <summary>单件节拍计时：从 WaitMaterial 检测到有料到 RecordOutput 完成（05 第 7.1 节）。</summary>
    public Stopwatch StepWatch { get; } = new();
}
