// 职责：贴附路径上的一个可编辑点（X / Y / Z 三个输入框 + 序号 + 校验状态）
// 层：App/ViewModels
// 依赖：TapePathPoint（Core/Models）、CommunityToolkit.Mvvm

using System.Globalization;
using AutoTaping.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoTaping.App.ViewModels;

/// <summary>
/// 贴附路径点编辑模型。坐标相对贴附基准（mm），Z 为该点的贴附高度，
/// 范围校验用与工艺参数一致的贴附高度范围（0 ~ 10 mm，见 09 文档 4.1）。
/// </summary>
public sealed partial class TapePathPointItemViewModel : ObservableObject
{
    /// <summary>Z（贴附高度）允许范围下限（mm）。</summary>
    private const double MinZmm = 0;

    /// <summary>Z（贴附高度）允许范围上限（mm），09 文档 4.1 节贴附高度范围。</summary>
    private const double MaxZmm = 10;

    /// <summary>X / Y 偏移允许范围（mm），相对贴附基准的小范围偏移。</summary>
    private const double MaxOffsetMm = 400;

    /// <summary>构造路径点。</summary>
    /// <param name="point">路径点（相对贴附基准的偏移）。</param>
    /// <param name="index">序号（从 1 开始）。</param>
    /// <param name="isReadOnly">是否只读。</param>
    public TapePathPointItemViewModel(TapePathPoint point, int index, bool isReadOnly)
    {
        _xt = point.X.ToString("F2", CultureInfo.InvariantCulture);
        _yt = point.Y.ToString("F2", CultureInfo.InvariantCulture);
        _zt = point.Z.ToString("F2", CultureInfo.InvariantCulture);
        Index = index;
        IsReadOnly = isReadOnly;
        Validate();
    }

    /// <summary>用户修改了本点坐标。</summary>
    public event EventHandler? ValueChanged;

    /// <summary>序号（从 1 开始，界面显示用）。</summary>
    public int Index { get; }

    /// <summary>X 偏移输入文本（mm）。</summary>
    [ObservableProperty]
    private string _xt;

    /// <summary>Y 偏移输入文本（mm）。</summary>
    [ObservableProperty]
    private string _yt;

    /// <summary>Z 贴附高度输入文本（mm）。</summary>
    [ObservableProperty]
    private string _zt;

    /// <summary>是否只读。</summary>
    [ObservableProperty]
    private bool _isReadOnly;

    /// <summary>是否存在非法输入。</summary>
    [ObservableProperty]
    private bool _hasError;

    /// <summary>取当前坐标；非法时返回 <see langword="false"/>。</summary>
    /// <param name="point">解析出的路径点。</param>
    public bool TryGetPoint(out TapePathPoint point)
    {
        var ok = TryReadCoordinates(out var x, out var y, out var z);
        point = ok ? new TapePathPoint(x, y, z) : default;
        return ok;
    }

    partial void OnXtChanged(string value) => OnCoordinateChanged();

    partial void OnYtChanged(string value) => OnCoordinateChanged();

    partial void OnZtChanged(string value) => OnCoordinateChanged();

    private void OnCoordinateChanged()
    {
        Validate();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Validate() => HasError = !TryReadCoordinates(out _, out _, out _);

    /// <summary>解析三个输入框并按范围校验（X/Y 为相对基准的偏移，Z 为贴附高度）。</summary>
    private bool TryReadCoordinates(out double x, out double y, out double z)
    {
        var ok = TryRead(Xt, out x);
        ok &= TryRead(Yt, out y);
        ok &= TryRead(Zt, out z);

        return ok
               && Math.Abs(x) <= MaxOffsetMm
               && Math.Abs(y) <= MaxOffsetMm
               && z >= MinZmm
               && z <= MaxZmm;
    }

    private static bool TryRead(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
