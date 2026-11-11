// 职责：SQLite 时间文本格式的统一转换（存 TEXT，格式 yyyy-MM-dd HH:mm:ss，见 06 文档第 10 节第 5 条）
// 层：Data
// 依赖：无

using System.Globalization;

namespace AutoTaping.Data;

/// <summary>
/// 时间列读写辅助。数据库所有时间列统一存 <c>TEXT</c>，格式 <c>yyyy-MM-dd HH:mm:ss</c>，
/// 不使用 SQLite 的 DateTime 隐式格式（带小数秒，会破坏字符串比较与排序）。
/// </summary>
internal static class SqliteTime
{
    /// <summary>时间列格式。</summary>
    private const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>日期列格式（ShiftDate 等只到日的列）。</summary>
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>时间 → 数据库文本。</summary>
    public static string ToDb(DateTime value) => value.ToString(DateTimeFormat, CultureInfo.InvariantCulture);

    /// <summary>可空时间 → 数据库文本（<see langword="null"/> 保持 <see langword="null"/>）。</summary>
    public static string? ToDb(DateTime? value) => value is null ? null : ToDb(value.Value);

    /// <summary>日期 → 数据库文本（只写日期部分）。</summary>
    public static string ToDbDate(DateTime value) => value.ToString(DateFormat, CultureInfo.InvariantCulture);

    /// <summary>数据库文本 → 时间；无法解析时抛 <see cref="FormatException"/>（数据被外部改坏时暴露问题，而不是静默当作默认值）。</summary>
    public static DateTime FromDb(string value)
        => DateTime.TryParseExact(value, DateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : throw new FormatException($"数据库时间列格式非法：'{value}'，期望 {DateTimeFormat}");

    /// <summary>数据库文本 → 日期（只到日的列，如 <c>ShiftProduction.ShiftDate</c>）。</summary>
    public static DateTime FromDbDate(string value)
        => DateTime.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : throw new FormatException($"数据库日期列格式非法：'{value}'，期望 {DateFormat}");
}
