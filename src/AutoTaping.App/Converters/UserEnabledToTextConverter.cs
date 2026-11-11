// 职责：用户启用状态 → 中文文字（启用 / 禁用）
// 层：App/Converters
// 依赖：无

using System.Globalization;
using System.Windows.Data;

namespace AutoTaping.App.Converters;

/// <summary>
/// 把 <see cref="bool"/> 启用状态映射为「启用 / 禁用」文字（系统设置页用户列表）。
/// </summary>
public sealed class UserEnabledToTextConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "启用" : "禁用";

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("用户启用状态文字不支持反向转换");
}
