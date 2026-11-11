// 职责：设备状态 → 状态灯画刷（颜色映射见 07-UI设计规范 第 6 节，颜色本身全部来自 Colors.xaml）
// 层：App/Converters
// 依赖：Colors.xaml（资源键）、DeviceState（Core/StateMachine）

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AutoTaping.Core.StateMachine;

namespace AutoTaping.App.Converters;

/// <summary>
/// 把 <see cref="DeviceState"/> 映射为状态灯画刷。只查资源字典、不写字面色值，
/// 因此换色板（07-UI设计规范第 2 节的浅色主题说明）时本文件无需修改。
/// </summary>
public sealed class DeviceStateToBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var brushKey = value is DeviceState state ? GetBrushKey(state) : "StatusIdle";
        return Application.Current?.TryFindResource(brushKey) as Brush ?? SystemColors.GrayTextBrush;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("设备状态灯颜色不支持反向转换");

    /// <summary>状态 → 色板资源键（07-UI设计规范 第 6 节的状态色映射表）。</summary>
    private static string GetBrushKey(DeviceState state) => state switch
    {
        DeviceState.Initializing => "StatusInfo",
        DeviceState.NotReady => "StatusIdle",
        DeviceState.Ready => "StatusRun",
        DeviceState.Running => "StatusRun",
        DeviceState.Paused => "StatusWait",
        DeviceState.Alarming => "StatusAlarm",
        DeviceState.EStopped => "StatusAlarm",
        DeviceState.Exiting => "StatusIdle",
        _ => "StatusIdle"
    };
}
