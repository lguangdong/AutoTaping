// 职责：班次划分规则（DAY / SWING / NIGHT）与时窗计算，产出记录与班次汇总共用
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 班次定义。<c>docs/06-数据模型与数据库.md</c> 规定班次为 <c>DAY</c> / <c>SWING</c> / <c>NIGHT</c>，
/// 本类给出唯一的时间划分（0~8 夜班、8~16 白班、16~24 中班），避免各处各写一套。
/// </summary>
public static class ShiftSchedule
{
    /// <summary>白班（08:00 ~ 16:00）代码。</summary>
    public const string Day = "DAY";

    /// <summary>中班（16:00 ~ 24:00）代码。</summary>
    public const string Swing = "SWING";

    /// <summary>夜班（00:00 ~ 08:00）代码。</summary>
    public const string Night = "NIGHT";

    /// <summary>全部班次代码（按时间顺序）。</summary>
    public static IReadOnlyList<string> All { get; } = [Night, Day, Swing];

    /// <summary>按时刻取班次代码。</summary>
    /// <param name="time">时刻。</param>
    public static string GetShiftName(DateTime time) => time.Hour switch
    {
        < 8 => Night,
        < 16 => Day,
        _ => Swing
    };

    /// <summary>取某个时刻所属班次的时间窗（左闭右开）。</summary>
    /// <param name="time">时刻。</param>
    public static (DateTime Start, DateTime End) GetShiftWindow(DateTime time)
    {
        var day = time.Date;

        return GetShiftName(time) switch
        {
            Night => (day, day.AddHours(8)),
            Day => (day.AddHours(8), day.AddHours(16)),
            _ => (day.AddHours(16), day.AddDays(1))
        };
    }

    /// <summary>班次代码转中文（界面显示用）。</summary>
    /// <param name="shift">班次代码。</param>
    public static string GetDisplayName(string shift) => shift switch
    {
        Day => "白班",
        Swing => "中班",
        Night => "夜班",
        _ => shift
    };
}
