// 职责：报警等级 → 中文文字（提示 / 警告 / 严重，见 09 文档第一节）
// 层：App/Converters
// 依赖：AlarmLevel（Core/Models）

using System.Globalization;
using System.Windows.Data;
using AutoTaping.Core.Models;

namespace AutoTaping.App.Converters;

/// <summary>
/// 把 <see cref="AlarmLevel"/> 映射为中文等级文字。报警页表格里直接绑定枚举时使用，
/// 避免在每一处 View 里重复写 DataTrigger。
/// </summary>
public sealed class AlarmLevelToTextConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AlarmLevel level
            ? level switch
            {
                AlarmLevel.Info => "提示",
                AlarmLevel.Warning => "警告",
                AlarmLevel.Critical => "严重",
                _ => level.ToString()
            }
            : "";

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("报警等级文字不支持反向转换");
}
