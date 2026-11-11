// 职责：单件产出记录实体（对应 SQLite OutputRecord 表），是产量统计与追溯的核心数据
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 每件产出记录。字段含义见 <c>docs/06-数据模型与数据库.md</c> 第 2 节 OutputRecord 表。
/// </summary>
public sealed class OutputRecord
{
    /// <summary>良品结果标记（数据库存储值）。</summary>
    public const string ResultOk = "OK";

    /// <summary>不良品结果标记（数据库存储值）。</summary>
    public const string ResultNg = "NG";

    /// <summary>主键，新增时由数据库生成。</summary>
    public int Id { get; set; }

    /// <summary>当日流水号，从 1 开始，由 <c>IOutputRepository.NextSequenceNo</c> 生成。</summary>
    public int SequenceNo { get; set; }

    /// <summary>产出时使用的配方 Id。</summary>
    public int RecipeId { get; set; }

    /// <summary>产出时使用的配方名（冗余存储，配方改名后仍可追溯）。</summary>
    public string RecipeName { get; set; } = "";

    /// <summary>结果：<see cref="ResultOk"/> 或 <see cref="ResultNg"/>。</summary>
    public string Result { get; set; } = ResultOk;

    /// <summary>节拍（ms），从检测到有料到落库完成。</summary>
    public int CycleTimeMs { get; set; }

    /// <summary>纠偏量 ΔX（mm）。</summary>
    public double DeltaX { get; set; }

    /// <summary>纠偏量 ΔY（mm）。</summary>
    public double DeltaY { get; set; }

    /// <summary>纠偏量 Δθ（°）。</summary>
    public double DeltaTheta { get; set; }

    /// <summary>MARK1 模板匹配得分。</summary>
    public double MatchScore1 { get; set; }

    /// <summary>MARK2 模板匹配得分。</summary>
    public double MatchScore2 { get; set; }

    /// <summary>NG 原因；OK 件为 <see langword="null"/>。</summary>
    public string? NgReason { get; set; }

    /// <summary>班次：<c>DAY</c> / <c>SWING</c> / <c>NIGHT</c>。</summary>
    public string Shift { get; set; } = "DAY";

    /// <summary>产出时间。</summary>
    public DateTime CreatedAt { get; set; }
}
