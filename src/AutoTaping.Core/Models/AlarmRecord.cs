// 职责：报警记录实体（对应 SQLite AlarmRecord 表）
// 层：Core/Models
// 依赖：AlarmLevel

namespace AutoTaping.Core.Models;

/// <summary>
/// 报警记录。报警码表见 <c>docs/09-报警与参数清单.md</c> 第二节，去重与恢复规则见第三节。
/// </summary>
public sealed class AlarmRecord
{
    /// <summary>主键，新增时由数据库生成。</summary>
    public int Id { get; set; }

    /// <summary>报警代码，如 <c>AL301</c>。</summary>
    public string Code { get; set; } = "";

    /// <summary>报警内容（面向操作工的中文描述）。</summary>
    public string Message { get; set; } = "";

    /// <summary>报警级别。</summary>
    public AlarmLevel Level { get; set; } = AlarmLevel.Info;

    /// <summary>报警来源，如 <c>贴附头</c> / <c>视觉</c>。</summary>
    public string Source { get; set; } = "";

    /// <summary>报警发生时间。</summary>
    public DateTime StartTime { get; set; }

    /// <summary>人工确认时间；未确认为 <see langword="null"/>。</summary>
    public DateTime? AckTime { get; set; }

    /// <summary>确认人用户名；未确认或系统自动恢复为 <see langword="null"/>。</summary>
    public string? AckUser { get; set; }

    /// <summary>恢复时间（报警条件消失）；仍在生效为 <see langword="null"/>。对应 SQL 列 <c>EndTime</c>。</summary>
    public DateTime? EndTime { get; set; }

    /// <summary>是否已人工确认。对应 SQL 列 <c>IsAcked</c>。</summary>
    public bool IsAcked { get; set; }
}
