// 职责：用户角色 → 中文文字（操作员 / 技术员 / 工程师，见 00 文档第 5 节）
// 层：App/Converters
// 依赖：UserRole（Core/Models）

using System.Globalization;
using System.Windows.Data;
using AutoTaping.Core.Models;

namespace AutoTaping.App.Converters;

/// <summary>
/// 把 <see cref="UserRole"/> 映射为中文角色文字。系统设置页的用户列表直接绑定枚举时使用，
/// 避免在 View 里重复写 DataTrigger（07-UI设计规范 第 9.1 节：样式与映射集中管理）。
/// </summary>
public sealed class UserRoleToTextConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is UserRole role
            ? role switch
            {
                UserRole.Operator => "操作员",
                UserRole.Technician => "技术员",
                UserRole.Engineer => "工程师",
                _ => role.ToString()
            }
            : "";

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("用户角色文字不支持反向转换");
}
