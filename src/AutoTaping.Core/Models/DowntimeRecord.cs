// 职责：停机记录实体（对应 SQLite DowntimeRecord 表），产量页停机分析的原始数据
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 一条停机记录。停机原因代码见 <see cref="DowntimeReasons"/>，
/// 分类口径见 <c>docs/05-核心状态机与流程.md</c> 第 7.2 节。
/// </summary>
public sealed class DowntimeRecord
{
    /// <summary>主键，新增时由数据库生成。</summary>
    public int Id { get; set; }

    /// <summary>停机原因代码（<see cref="DowntimeReasons"/> 之一）。</summary>
    public string Reason { get; set; } = DowntimeReasons.Fault;

    /// <summary>停机开始时间。</summary>
    public DateTime StartTime { get; set; }

    /// <summary>恢复时间；未知（如断电补记）为 <see langword="null"/>。</summary>
    public DateTime? EndTime { get; set; }

    /// <summary>停机时长（秒）；恢复时间未知时为 0。</summary>
    public int DurationSeconds { get; set; }
}
