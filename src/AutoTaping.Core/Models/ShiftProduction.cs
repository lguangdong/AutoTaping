// 职责：班次产量汇总实体（对应 SQLite ShiftProduction 表），每天每班一条
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 班次产量汇总。<c>ShiftDate + ShiftName</c> 唯一（见 06 文档表定义）。
/// </summary>
public sealed class ShiftProduction
{
    /// <summary>主键，新增时由数据库生成。</summary>
    public int Id { get; set; }

    /// <summary>班次日期（只用日期部分，数据库存 <c>yyyy-MM-dd</c>）。</summary>
    public DateTime ShiftDate { get; set; }

    /// <summary>班次名：<c>DAY</c> / <c>SWING</c> / <c>NIGHT</c>。</summary>
    public string ShiftName { get; set; } = "DAY";

    /// <summary>总产出件数。</summary>
    public int TotalCount { get; set; }

    /// <summary>良品件数。</summary>
    public int OkCount { get; set; }

    /// <summary>不良品件数。</summary>
    public int NgCount { get; set; }

    /// <summary>运行时长（秒）。</summary>
    public int RunSeconds { get; set; }

    /// <summary>停机时长（秒）。</summary>
    public int DownSeconds { get; set; }

    /// <summary>最后更新时间。</summary>
    public DateTime LastUpdateAt { get; set; }
}
