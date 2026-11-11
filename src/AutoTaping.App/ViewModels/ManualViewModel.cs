// 职责：手动调试页 ViewModel——4 轴面板、气缸/真空/吹气/压合控制、只读 IO 网格、200ms 快照刷新与离开清理
// 层：App/ViewModels
// 依赖：IMotionCard、IIoService、IAuthService、IDeviceMonitor、IUiDispatcher、IUserNotifier（Core/Abstractions）、
//       IoPointNames / DeviceSnapshot（Core/Models）、ManualOptions / IoPointMap（App/Infrastructure）、communityToolkit.Mvvm
//
// 说明：本类不创建窗口、不弹 MessageBox（走 IUserNotifier），所有界面属性只在 UI 线程赋值。

using System.Collections.ObjectModel;
using AutoTaping.App.Infrastructure;
using AutoTaping.App.Navigation;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Exceptions;
using AutoTaping.Core.Models;
using AutoTaping.Core.StateMachine;
using AutoTaping.Hardware.Motion;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AutoTaping.App.ViewModels;

/// <summary>
/// 手动调试页 ViewModel（08-页面清单 页面 2）。技术员及以上可进，进入前必须二次确认；
/// 页面顶部永久红色安全横幅；离开页面时停止点动、清除 IO 强制输出、把 Z 轴抬到安全高度。
/// </summary>
public sealed partial class ManualViewModel : PageViewModel
{
    /// <summary>界面刷新周期（ms），与采集线程的 200ms 周期一致（11-开发任务分解 S4 交付物）。</summary>
    private const int RefreshIntervalMs = 200;

    private readonly IMotionCard _motion;
    private readonly IIoService _io;
    private readonly IAuthService _auth;
    private readonly IDeviceMonitor _monitor;
    private readonly IUiDispatcher _ui;
    private readonly IUserNotifier _notifier;
    private readonly IAutoFlowCoordinator _coordinator;
    private readonly ManualOptions _options;
    private readonly ILogger<ManualViewModel> _log;
    private readonly CancellationTokenSource _lifetime = new();

    private CancellationTokenSource? _refreshCts;

    /// <summary>构造手动页 ViewModel（由 DI 单例创建，页面进入时才开始刷新）。</summary>
    /// <param name="motion">运控卡抽象。</param>
    /// <param name="io">IO 服务抽象。</param>
    /// <param name="auth">认证与权限服务（ViewModel 层二次校验）。</param>
    /// <param name="monitor">设备采集器（200ms 快照）。</param>
    /// <param name="ui">UI 线程调度（采集结果切回 UI 线程）。</param>
    /// <param name="notifier">用户提示通道。</param>
    /// <param name="options">手动页参数（速度、软限位、安全高度）。</param>
    /// <param name="pointMap">IO 点位中文名映射。</param>
    /// <param name="loggerFactory">日志工厂（为每个轴面板创建独立日志分类）。</param>
    public ManualViewModel(IMotionCard motion,
                           IIoService io,
                           IAuthService auth,
                           IDeviceMonitor monitor,
                           IUiDispatcher ui,
                           IUserNotifier notifier,
                           IAutoFlowCoordinator coordinator,
                           ManualOptions options,
                           IoPointMap pointMap,
                           ILoggerFactory loggerFactory)
    {
        _motion = motion;
        _io = io;
        _auth = auth;
        _monitor = monitor;
        _ui = ui;
        _notifier = notifier;
        _coordinator = coordinator;
        _options = options;
        _log = loggerFactory.CreateLogger<ManualViewModel>();

        Axes = new[] { AxisId.X, AxisId.Y, AxisId.Z, AxisId.R }
            .Select(axis => new AxisControlViewModel(
                axis,
                options.MinLimits[(int)axis],
                options.MaxLimits[(int)axis],
                options.DefaultSpeed,
                options.MinSpeed,
                options.MaxSpeed,
                motion,
                notifier,
                loggerFactory.CreateLogger<AxisControlViewModel>()))
            .ToList();

        foreach (var point in pointMap.Di)
        {
            DiPoints.Add(new IoPointViewModel(point.Point, point.Name));
        }

        foreach (var point in pointMap.Do)
        {
            DoPoints.Add(new IoPointViewModel(point.Point, point.Name));
        }
    }

    /// <inheritdoc />
    public override PageKey Key => PageKey.Manual;

    /// <inheritdoc />
    public override string Title => "手动调试";

    /// <inheritdoc />
    public override string Icon => "✥";

    /// <inheritdoc />
    public override Permission RequiredPermission => Permission.ManualMode;

    /// <summary>4 个轴面板（顺序 X / Y / Z / R，界面按 2×2 排布）。</summary>
    public IReadOnlyList<AxisControlViewModel> Axes { get; }

    /// <summary>只读输入点位（DI）网格。</summary>
    public ObservableCollection<IoPointViewModel> DiPoints { get; } = new();

    /// <summary>只读输出点位（DO）网格。</summary>
    public ObservableCollection<IoPointViewModel> DoPoints { get; } = new();

    /// <summary>是否处于急停状态（急停时本页所有控制禁用，11-开发任务分解 S4 验收第 3 条）。</summary>
    [ObservableProperty]
    private bool _isEmergencyStopped;

    /// <summary>运控卡是否已连接（未连接时禁止一切运动，05-核心状态机 互锁第 5 条）。</summary>
    [ObservableProperty]
    private bool _isMotionConnected = true;

    /// <summary>进入本页：校验权限 → 二次确认 → 启动 200ms 刷新。</summary>
    /// <param name="ct">取消令牌。</param>
    public override Task<bool> OnEnteringAsync(CancellationToken ct)
    {
        if (!_auth.HasPermission(Permission.ManualMode))
        {
            _log.LogWarning("权限不足，拒绝进入手动页：用户 {User}", _auth.CurrentUser?.UserName);
            _notifier.Warn("进入「手动调试」需要技术员权限，请切换到技术员账户后再试。");
            return Task.FromResult(false);
        }

        if (_coordinator.State is DeviceState.Running or DeviceState.Paused)
        {
            _log.LogWarning("设备运行中，拒绝进入手动页");
            _notifier.Warn("设备运行中，禁止切换到手动调试页，请先停止自动流程。");
            return Task.FromResult(false);
        }

        if (!_notifier.Confirm("进入手动模式", "进入手动模式后自动流程将停止，确认进入？"))
        {
            _log.LogInformation("用户取消进入手动模式");
            return Task.FromResult(false);
        }

        _coordinator.TrySwitchMode(OperationMode.Manual);

        _log.LogInformation("进入手动模式：用户 {User}", _auth.CurrentUser?.UserName);
        ApplySnapshot(_monitor.Current);
        StartRefreshLoop();
        return Task.FromResult(true);
    }

    /// <summary>离开本页：停刷新 → 停点动 → 清强制输出 → Z 轴抬到安全高度。</summary>
    /// <param name="ct">取消令牌。</param>
    public override Task OnLeaveAsync(CancellationToken ct)
    {
        StopRefreshLoop();

        foreach (var axis in Axes)
        {
            axis.StopMotionOnLeave();
        }

        try
        {
            _io.ClearAllForcedOutput();
        }
        catch (HardwareException ex)
        {
            _log.LogError(ex, "离开手动页时清除 IO 强制输出失败");
        }

        LiftZToSafeHeight();

        _coordinator.TrySwitchMode(OperationMode.Auto);

        _log.LogInformation("离开手动页：已停止点动、清除 IO 强制输出、Z 轴抬升至安全高度 {Height:F2}mm",
                            _options.SafeHeightMm);
        return Task.CompletedTask;
    }

    /// <summary>红色横幅上的「退出手动」：有轴在运动时拒绝切回自动，否则请求切回自动运行页。</summary>
    [RelayCommand]
    private void LeavePage()
    {
        var movingAxis = Axes.FirstOrDefault(axis => axis.IsMoving);
        if (movingAxis is not null)
        {
            // 08-页面清单 页面 2：切回自动时若有轴在运动，必须拒绝并提示（防止带着未完成的动作回到自动流程）
            _log.LogWarning("拒绝切回自动：{Axis} 仍在运动", movingAxis.AxisName);
            _notifier.Warn($"{movingAxis.AxisName} 正在运动，请等轴停止后再切回自动运行。");
            return;
        }

        _log.LogInformation("用户点击「退出手动」，请求切回自动运行页");
        RequestNavigation(PageKey.Auto);
    }

    /// <summary>开真空（Y00）。</summary>
    [RelayCommand(CanExecute = nameof(CanOperateValve))]
    private void VacuumOn() => WriteDo(IoPointNames.VacuumValve, true, "开真空");

    /// <summary>关真空（Y00）。</summary>
    [RelayCommand(CanExecute = nameof(CanOperateValve))]
    private void VacuumOff() => WriteDo(IoPointNames.VacuumValve, false, "关真空");

    /// <summary>开吹气 / 破真空（Y01）。</summary>
    [RelayCommand(CanExecute = nameof(CanOperateValve))]
    private void BlowOn() => WriteDo(IoPointNames.BlowValve, true, "开吹气");

    /// <summary>关吹气（Y01）。</summary>
    [RelayCommand(CanExecute = nameof(CanOperateValve))]
    private void BlowOff() => WriteDo(IoPointNames.BlowValve, false, "关吹气");

    /// <summary>压合气缸下压（Y02）。</summary>
    [RelayCommand(CanExecute = nameof(CanOperateValve))]
    private void PressDown() => WriteDo(IoPointNames.PressCylinder, true, "压合下压");

    /// <summary>压合气缸抬起（Y02）。</summary>
    [RelayCommand(CanExecute = nameof(CanOperateValve))]
    private void PressUp() => WriteDo(IoPointNames.PressCylinder, false, "压合抬起");

    /// <inheritdoc />
    protected override void DisposeCore()
    {
        StopRefreshLoop();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private bool CanOperateValve() => IsMotionConnected && !IsEmergencyStopped;

    private void WriteDo(string point, bool on, string action)
    {
        try
        {
            _io.WriteDo(point, on);
            _log.LogInformation("手动操作：{Action}（{Point} = {State}）", action, point, on ? "ON" : "OFF");
            SetLocalDoState(point, on);
        }
        catch (HardwareException ex)
        {
            _log.LogError(ex, "手动 IO 写入失败：{Action}（{Point}）", action, point);
            _notifier.Warn($"{action}失败：{ex.Message}");
        }
    }

    /// <summary>写入后立即回显，避免等到下一帧快照才有反馈（7 个点位，开销可忽略）。</summary>
    private void SetLocalDoState(string point, bool on)
    {
        foreach (var item in DoPoints)
        {
            if (string.Equals(item.Point, point, StringComparison.Ordinal))
            {
                item.IsOn = on;
                return;
            }
        }
    }

    private void LiftZToSafeHeight()
    {
        var target = Math.Clamp(_options.SafeHeightMm,
                                _options.MinLimits[(int)AxisId.Z],
                                _options.MaxLimits[(int)AxisId.Z]);

        try
        {
            // 不阻塞切页：Z 抬升是单调的安全动作，下发后由采集快照显示实际位置
            _motion.MoveAbsolute(AxisId.Z, target, _options.DefaultSpeed);
            _log.LogDebug("Z 轴抬升至安全高度 {Height:F2}mm", target);
        }
        catch (HardwareException ex)
        {
            _log.LogError(ex, "离开手动页时 Z 轴抬升失败，请人工确认 Z 轴位置");
            _notifier.Warn("Z 轴未抬到安全高度，请在切回自动/断电前人工确认 Z 轴位置。");
        }
    }

    /// <summary>启动 200ms 刷新循环（重复进入页面会先停掉旧循环，避免累积多个任务）。</summary>
    private void StartRefreshLoop()
    {
        StopRefreshLoop();

        _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _refreshCts.Token;
        _ = Task.Run(() => RefreshLoopAsync(token), token);
    }

    private void StopRefreshLoop()
    {
        var cts = _refreshCts;
        if (cts is null)
        {
            return;
        }

        _refreshCts = null;
        cts.Cancel();
        cts.Dispose();
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(RefreshIntervalMs));

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var snapshot = _monitor.Current;
                await _ui.InvokeAsync(() => ApplySnapshot(snapshot)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 切页/退出时取消刷新属于正常路径，不记录异常堆栈以免刷满日志
            _log.LogDebug("手动页刷新循环已停止");
        }
        catch (Exception ex)
        {
            // 刷新循环异常只影响本页显示，不得让界面崩溃（01-架构设计 第 6 节）
            _log.LogError(ex, "手动页刷新循环异常终止，本页数值将停止刷新");
        }
    }

    /// <summary>把一帧快照写入界面（必须在 UI 线程调用）。</summary>
    private void ApplySnapshot(DeviceSnapshot snapshot)
    {
        IsMotionConnected = snapshot.MotionConnected;

        var emergencyStopped = DetectEmergencyStop(snapshot);
        if (emergencyStopped != IsEmergencyStopped)
        {
            IsEmergencyStopped = emergencyStopped;
            if (emergencyStopped)
            {
                _log.LogWarning("检测到急停状态，手动页全部控制已禁用");
            }
        }

        var controlEnabled = snapshot.MotionConnected && !emergencyStopped;

        foreach (var axis in Axes)
        {
            axis.IsControlEnabled = controlEnabled;
            axis.ApplySnapshot(snapshot);
        }

        foreach (var item in DiPoints)
        {
            item.Apply(snapshot.DiStates, null);
        }

        foreach (var item in DoPoints)
        {
            item.Apply(snapshot.DoStates, snapshot.ForcedPoints);
        }
    }

    /// <summary>
    /// 急停判定：任一轴停止原因为急停，或急停输入 X06 = 0（05-核心状态机 第 5.2 节、00-项目总览 1.3 常闭）。
    /// 点表里没有 X06 时不据此判定，避免配置缺失导致整页被误禁。
    /// </summary>
    private bool DetectEmergencyStop(DeviceSnapshot snapshot)
    {
        foreach (var axis in Axes)
        {
            if (snapshot.GetStopReason(axis.Axis) == MotionErrorCodes.EmergencyStop)
            {
                return true;
            }
        }

        return snapshot.DiStates.TryGetValue(IoPointNames.EmergencyStopReleased, out var released) && !released;
    }

    partial void OnIsEmergencyStoppedChanged(bool value) => RaiseValveCanExecuteChanged();

    partial void OnIsMotionConnectedChanged(bool value) => RaiseValveCanExecuteChanged();

    private void RaiseValveCanExecuteChanged()
    {
        VacuumOnCommand.NotifyCanExecuteChanged();
        VacuumOffCommand.NotifyCanExecuteChanged();
        BlowOnCommand.NotifyCanExecuteChanged();
        BlowOffCommand.NotifyCanExecuteChanged();
        PressDownCommand.NotifyCanExecuteChanged();
        PressUpCommand.NotifyCanExecuteChanged();
    }
}
