// 职责：bool → Visibility 转换（支持 ConverterParameter=Invert 反向），用于提示文字/错误行的显隐
// 层：App/Converters
// 依赖：无（只用 System.Windows 基础类型）

using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AutoTaping.App.Converters;

/// <summary>
/// 布尔值转 <see cref="Visibility"/>：<see langword="true"/> → <see cref="Visibility.Visible"/>，
/// 传 <c>ConverterParameter="Invert"</c> 时取反（用于"权限不足才显示提示"这类场景）。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>表示反向的转换参数。</summary>
    private const string InvertParameter = "Invert";

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (IsInvert(parameter))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("显隐状态不支持反向转换");

    private static bool IsInvert(object? parameter)
        => parameter is string text && string.Equals(text, InvertParameter, StringComparison.OrdinalIgnoreCase);
}
