// 职责：报警等级 → 等级文字前景色（提示=黄、警告=橙、严重=红，映射见 09 文档第一节）
// 层：App/Converters
// 依赖：Colors.xaml（资源键）、AlarmLevel（Core/Models）

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AutoTaping.Core.Models;

namespace AutoTaping.App.Converters;

/// <summary>
/// 把 <see cref="AlarmLevel"/> 映射为文字画刷。只查资源字典、不写字面色值，
/// 换色板时本文件无需修改（07-UI设计规范 第 11 节约束 1）。
/// </summary>
public sealed class AlarmLevelToBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var brushKey = value is AlarmLevel level ? GetBrushKey(level) : "TextSecondary";
        return Application.Current?.TryFindResource(brushKey) as Brush ?? SystemColors.GrayTextBrush;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("报警等级颜色不支持反向转换");

    /// <summary>报警等级 → 色板资源键（09-报警与参数清单 第一节）。</summary>
    private static string GetBrushKey(AlarmLevel level) => level switch
    {
        AlarmLevel.Critical => "StatusAlarm",
        AlarmLevel.Warning => "StatusWarn",
        AlarmLevel.Info => "StatusWait",
        _ => "TextSecondary"
    };
}
