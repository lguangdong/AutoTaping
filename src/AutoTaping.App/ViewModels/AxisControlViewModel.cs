// 职责：手动页单个轴的面板 ViewModel（点动 / 回零 / 定位 / 速度与软限位校验 / 状态显示）
// 层：App/ViewModels
// 依赖：IMotionCard、IUserNotifier（Core/Abstractions）、HardwareException（Core/Exceptions）、
//       DeviceSnapshot（Core/Models）、CommunityToolkit.Mvvm、ILogger

using System.Globalization;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Exceptions;
using AutoTaping.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AutoTaping.App.ViewModels;

/// <summary>
/// 手动页一个轴的控制面板。交互规则来自 08-页面清单 页面 2：
/// 点动按住才动、松开立即停；超软限位时输入框红框且定位按钮禁用；未回零时禁止定位；急停时全部禁用。
/// 本类不做权限判断（页面级权限已在进入时校验），也不直接读写界面（属性由采集循环在 UI 线程赋值）。
/// </summary>
public sealed partial class AxisControlViewModel : ObservableObject
{
    private readonly IMotionCard _motion;
    private readonly IUserNotifier _notifier;
    private readonly ILogger _log;
    private readonly double _minLimit;
    private readonly double _maxLimit;
    private readonly double _minSpeed;
    private readonly double _maxSpeed;

    /// <summary>已通过校验的目标位置；<see langword="null"/> 表示当前输入不可用于定位。</summary>
    private double? _targetValue;

    private bool _isJogging;
    private bool _isHoming;

    /// <summary>构造一个轴面板。</summary>
    /// <param name="axis">轴标识。</param>
    /// <param name="minLimit">软限位下限（来自 appsettings，mm；R 轴为 °）。</param>
    /// <param name="maxLimit">软限位上限。</param>
    /// <param name="defaultSpeed">默认速度（mm/s；R 轴为 °/s）。</param>
    /// <param name="minSpeed">手动速度下限。</param>
    /// <param name="maxSpeed">手动速度上限。</param>
    /// <param name="motion">运控卡抽象。</param>
    /// <param name="notifier">用户提示通道（ViewModel 不得直接弹 MessageBox）。</param>
    /// <param name="log">日志。</param>
    public AxisControlViewModel(AxisId axis, double minLimit, double maxLimit, double defaultSpeed,
                                double minSpeed, double maxSpeed,
                                IMotionCard motion, IUserNotifier notifier, ILogger log)
    {
        Axis = axis;
        _minLimit = minLimit;
        _maxLimit = maxLimit;
        _minSpeed = minSpeed;
        _maxSpeed = maxSpeed;
        _motion = motion;
        _notifier = notifier;
        _log = log;

        AxisName = $"{axis} 轴";
        Unit = axis == AxisId.R ? "°" : "mm";
        SpeedUnit = axis == AxisId.R ? "°/s" : "mm/s";
        LimitText = $"{minLimit:F2} ~ {maxLimit:F2} {Unit}";
        _speedText = defaultSpeed.ToString("F1", CultureInfo.InvariantCulture);
    }

    /// <summary>轴标识。</summary>
    public AxisId Axis { get; }

    /// <summary>轴显示名（如「X 轴」）。</summary>
    public string AxisName { get; }

    /// <summary>位置单位（X/Y/Z 为 mm，R 为 °）。</summary>
    public string Unit { get; }

    /// <summary>速度单位（X/Y/Z 为 mm/s，R 为 °/s）。</summary>
    public string SpeedUnit { get; }

    /// <summary>软限位显示文本（如「0.00 ~ 400.00 mm」）。</summary>
    public string LimitText { get; }

    /// <summary>当前位置（mm / °），由采集快照驱动。</summary>
    [ObservableProperty]
    private double _position;

    /// <summary>速度输入文本（允许用户直接键入，故用字符串承载，校验在 setter 之后进行）。</summary>
    [ObservableProperty]
    private string _speedText = "";

    /// <summary>目标位置输入文本。</summary>
    [ObservableProperty]
    private string _targetText = "";

    /// <summary>是否已回零（未回零时禁止绝对定位，08-页面清单 页面 2 交互规则）。</summary>
    [ObservableProperty]
    private bool _isHomed;

    /// <summary>轴是否在动（含点动、回零、定位）。</summary>
    [ObservableProperty]
    private bool _isMoving;

    /// <summary>是否正在执行回零/定位（点动不算，否则按住按钮会在按下瞬间被禁用）。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>本轴控制是否可用（急停或运控卡未连接时为 <see langword="false"/>）。</summary>
    [ObservableProperty]
    private bool _isControlEnabled = true;

    /// <summary>目标位置输入是否非法（为 <see langword="true"/> 时输入框显示红框）。</summary>
    [ObservableProperty]
    private bool _hasTargetError;

    /// <summary>目标位置错误原因（带允许范围，09-报警与参数清单 第五节：提示必须带范围）。</summary>
    [ObservableProperty]
    private string _targetError = "";

    /// <summary>速度输入是否非法。</summary>
    [ObservableProperty]
    private bool _hasSpeedError;

    /// <summary>速度错误原因（带允许范围）。</summary>
    [ObservableProperty]
    private string _speedError = "";

    /// <summary>轴状态文字（运动中 / 回零中 / 已回零 / 未回零 / 控制已禁用）。</summary>
    [ObservableProperty]
    private string _stateText = "未回零";

    /// <summary>负向点动（08-页面清单：按住才动，松开由 <see cref="StopJogCommand"/> 停止）。</summary>
    [RelayCommand(CanExecute = nameof(CanJog))]
    private void JogNegative() => StartJog(-1);

    /// <summary>正向点动。</summary>
    [RelayCommand(CanExecute = nameof(CanJog))]
    private void JogPositive() => StartJog(1);

    /// <summary>停止点动。急停时也必须能执行（否则松开按钮后轴仍在动），因此不设 CanExecute。</summary>
    [RelayCommand]
    private void StopJog()
    {
        if (!_isJogging)
        {
            return;
        }

        _isJogging = false;
        StopAxisSafely("停止点动");
    }

    /// <summary>回零（二次确认后执行，进度由采集快照回显）。</summary>
    [RelayCommand(CanExecute = nameof(CanHome))]
    private void Home()
    {
        if (!_notifier.Confirm("确认回零",
                $"即将执行 {AxisName} 回零。\n回零过程中该轴会移动，请确认设备内无人、无障碍物。"))
        {
            _log.LogInformation("用户取消 {Axis} 轴回零", Axis);
            return;
        }

        try
        {
            _motion.Home(Axis);
            _isHoming = true;
            _log.LogInformation("手动回零：轴 {Axis}", Axis);
        }
        catch (HardwareException ex)
        {
            _log.LogError(ex, "轴 {Axis} 回零指令被拒绝", Axis);
            _notifier.Warn($"{AxisName} 回零失败：{ex.Message}");
        }
    }

    /// <summary>定位到输入的目标位置。</summary>
    [RelayCommand(CanExecute = nameof(CanMoveToTarget))]
    private void MoveToTarget()
    {
        if (_targetValue is not { } target)
        {
            return;
        }

        if (!TryGetSpeed(out var speed))
        {
            return;
        }

        // 下发前再校验一次软限位：输入框校验与命令执行之间用户可能改过参数（防御性检查）
        if (target < _minLimit || target > _maxLimit)
        {
            HasTargetError = true;
            TargetError = $"目标超软限位：允许 {LimitText}";
            return;
        }

        try
        {
            _motion.MoveAbsolute(Axis, target, speed);
            _isHoming = false;
            _log.LogInformation("手动定位：轴 {Axis} 目标 {Target:F2} 速度 {Speed:F1}",
                                Axis, target, speed);
        }
        catch (HardwareException ex)
        {
            _log.LogError(ex, "轴 {Axis} 定位被拒绝，目标 {Target:F2}", Axis, target);
            _notifier.Warn($"{AxisName} 定位失败：{ex.Message}");
        }
    }

    /// <summary>用最新采集快照刷新位置、回零与运动状态（必须在 UI 线程调用）。</summary>
    /// <param name="snapshot">采集线程产出的快照。</param>
    public void ApplySnapshot(DeviceSnapshot snapshot)
    {
        Position = snapshot.GetPosition(Axis);
        IsHomed = snapshot.IsHomed(Axis);

        var moving = !snapshot.IsInPosition(Axis);
        IsMoving = moving;

        if (!moving)
        {
            // 回零/定位结束（到位、被停止或报警停止）后清掉进行中标记
            _isHoming = false;
        }

        IsBusy = moving && !_isJogging;
        UpdateStateText();
    }

    /// <summary>离开手动页时停止本轴的点动（08-页面清单 页面 2 安全要求 3）。</summary>
    public void StopMotionOnLeave()
    {
        if (!_isJogging)
        {
            return;
        }

        _isJogging = false;
        StopAxisSafely("离开手动页停止点动");
    }

    private void StartJog(int direction)
    {
        if (!TryGetSpeed(out var speed))
        {
            return;
        }

        try
        {
            _motion.JogStart(Axis, direction, speed);
            _isJogging = true;
            _log.LogDebug("手动点动：轴 {Axis} 方向 {Direction} 速度 {Speed:F1}", Axis, direction, speed);
        }
        catch (HardwareException ex)
        {
            _isJogging = false;
            _log.LogError(ex, "轴 {Axis} 点动被拒绝", Axis);
            _notifier.Warn($"{AxisName} 点动失败：{ex.Message}");
        }
    }

    /// <summary>停止本轴。停止本身失败只记日志，不再弹窗（避免松开按钮时连环弹窗）。</summary>
    private void StopAxisSafely(string reason)
    {
        try
        {
            _motion.Stop(Axis, emergency: false);
            _log.LogDebug("{Reason}：轴 {Axis}", reason, Axis);
        }
        catch (HardwareException ex)
        {
            _log.LogError(ex, "{Reason}：轴 {Axis} 停止指令失败，请立即人工检查", reason, Axis);
        }
    }

    private bool CanJog() => IsControlEnabled && !IsBusy && !HasSpeedError;

    private bool CanHome() => IsControlEnabled && !IsBusy;

    private bool CanMoveToTarget()
        => IsControlEnabled && !IsBusy && IsHomed && !HasTargetError && !HasSpeedError && _targetValue.HasValue;

    private void UpdateStateText()
    {
        if (!IsControlEnabled)
        {
            StateText = "控制已禁用";
        }
        else if (_isHoming)
        {
            StateText = "回零中";
        }
        else if (IsBusy)
        {
            StateText = "运动中";
        }
        else if (!IsHomed)
        {
            StateText = "未回零";
        }
        else
        {
            StateText = "已回零";
        }
    }

    private bool TryGetSpeed(out double speed)
    {
        if (!TryParseNumber(SpeedText, out speed))
        {
            HasSpeedError = true;
            SpeedError = $"速度应在 {_minSpeed:F1} ~ {_maxSpeed:F1} {SpeedUnit}";
            return false;
        }

        return true;
    }

    /// <summary>目标位置校验：非数字、超软限位都置红框并禁用定位按钮。</summary>
    partial void OnTargetTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _targetValue = null;
            HasTargetError = false;
            TargetError = "";
            return;
        }

        if (!TryParseNumber(value, out var target))
        {
            _targetValue = null;
            HasTargetError = true;
            TargetError = "请输入数字，例如 150.00";
            return;
        }

        if (target < _minLimit || target > _maxLimit)
        {
            _targetValue = null;
            HasTargetError = true;
            TargetError = $"目标超软限位：允许 {LimitText}";
            return;
        }

        _targetValue = target;
        HasTargetError = false;
        TargetError = "";
    }

    /// <summary>速度校验：超出手动限速范围时提示并禁用点动/定位。</summary>
    partial void OnSpeedTextChanged(string value)
    {
        if (TryParseNumber(value, out var speed) && speed >= _minSpeed && speed <= _maxSpeed)
        {
            HasSpeedError = false;
            SpeedError = "";
            return;
        }

        HasSpeedError = true;
        SpeedError = $"速度应在 {_minSpeed:F1} ~ {_maxSpeed:F1} {SpeedUnit}";
    }

    partial void OnIsControlEnabledChanged(bool value)
    {
        UpdateStateText();
        RaiseCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) => RaiseCanExecuteChanged();

    partial void OnIsHomedChanged(bool value) => RaiseCanExecuteChanged();

    partial void OnHasTargetErrorChanged(bool value) => RaiseCanExecuteChanged();

    partial void OnHasSpeedErrorChanged(bool value) => RaiseCanExecuteChanged();

    private void RaiseCanExecuteChanged()
    {
        JogNegativeCommand.NotifyCanExecuteChanged();
        JogPositiveCommand.NotifyCanExecuteChanged();
        HomeCommand.NotifyCanExecuteChanged();
        MoveToTargetCommand.NotifyCanExecuteChanged();
    }

    /// <summary>数字解析：先按不变文化（小数点为 .）解析，再按当前文化，兼容现场两种键入习惯。</summary>
    private static bool TryParseNumber(string? text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
           || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
}
