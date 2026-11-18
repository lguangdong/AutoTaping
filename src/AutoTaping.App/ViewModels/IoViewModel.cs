// 职责：IO 监控页 ViewModel——DI/DO 网格（绿=ON 深灰=OFF、中文名）、200ms 刷新、工程师强制输出与退出清除
// 层：App/ViewModels
// 依赖：IIoService、IAuthService、IDeviceMonitor、IUiDispatcher、IUserNotifier（Core/Abstractions）、
//       DeviceSnapshot（Core/Models）、IoPointMap（App/Infrastructure）、CommunityToolkit.Mvvm

using System.Collections.ObjectModel;
using AutoTaping.App.Infrastructure;
using AutoTaping.App.Navigation;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Exceptions;
using AutoTaping.Core.Models;
using AutoTaping.Hardware.Io;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AutoTaping.App.ViewModels;

/// <summary>
/// IO 监控页 ViewModel（08-页面清单 页面 7）。技术员及以上可进；强制输出仅工程师可用，
/// 退出页面或切换页面时自动清除全部强制输出（避免现场把设备留在强制状态）。
/// </summary>
public sealed partial class IoViewModel : PageViewModel
{
    /// <summary>界面刷新周期（ms），与采集线程一致。</summary>
    private const int RefreshIntervalMs = 200;

    /// <summary>强制状态下拉项：写入 ON。</summary>
    private const string ForceOnText = "ON";

    /// <summary>强制状态下拉项：写入 OFF。</summary>
    private const string ForceOffText = "OFF";

    /// <summary>本地写入后等待采集快照确认的最长时间（ms）。采集周期 200ms，留足余量。</summary>
    private const int PendingHoldMs = 2_000;

    private readonly IIoService _io;

    /// <summary>虚拟 IO 实现；真实硬件模式为 <see langword="null"/>（不提供输入模拟）。</summary>
    private readonly VirtualIoService? _virtualIo;

    private readonly IAuthService _auth;
    private readonly IDeviceMonitor _monitor;
    private readonly IUiDispatcher _ui;
    private readonly IUserNotifier _notifier;
    private readonly ILogger<IoViewModel> _log;
    private readonly CancellationTokenSource _lifetime = new();

    private CancellationTokenSource? _refreshCts;

    /// <summary>本地刚写入、采集快照尚未追上的点位（防止 200ms 刷新用旧值覆盖显示）。</summary>
    private readonly Dictionary<string, PendingState> _pendingStates = new(StringComparer.Ordinal);

    private readonly object _pendingSync = new();

    /// <summary>本地待确认的点位状态与过期时刻（<see cref="Environment.TickCount64"/>，单调不受系统时间影响）。</summary>
    private readonly record struct PendingState(bool Value, long ExpireTick);

    /// <summary>构造 IO 页 ViewModel（页面进入时才开始刷新）。</summary>
    /// <param name="io">IO 服务抽象。</param>
    /// <param name="auth">认证与权限服务（强制输出前二次校验工程师权限）。</param>
    /// <param name="monitor">设备采集器。</param>
    /// <param name="ui">UI 线程调度。</param>
    /// <param name="notifier">用户提示通道。</param>
    /// <param name="pointMap">IO 点位中文名映射。</param>
    /// <param name="log">日志。</param>
    public IoViewModel(IIoService io,
                       IAuthService auth,
                       IDeviceMonitor monitor,
                       IUiDispatcher ui,
                       IUserNotifier notifier,
                       IoPointMap pointMap,
                       ILogger<IoViewModel> log)
    {
        _io = io;
        _virtualIo = io as VirtualIoService;
        _auth = auth;
        _monitor = monitor;
        _ui = ui;
        _notifier = notifier;
        _log = log;

        foreach (var point in pointMap.Di)
        {
            DiPoints.Add(new IoPointViewModel(point.Point, point.Name));
        }

        foreach (var point in pointMap.Do)
        {
            DoPoints.Add(new IoPointViewModel(point.Point, point.Name));
        }

        // 默认选中第一个输入点（X00 载具到位），虚拟模式下放行自动流程时少一步选择
        SelectedDiPoint = DiPoints.Count > 0 ? DiPoints[0] : null;
    }

    /// <inheritdoc />
    public override PageKey Key => PageKey.Io;

    /// <inheritdoc />
    public override string Title => "IO 监控";

    /// <inheritdoc />
    public override string Icon => "⊞";

    /// <inheritdoc />
    public override Permission RequiredPermission => Permission.ManualMode;

    /// <summary>输入点位（只读）。</summary>
    public ObservableCollection<IoPointViewModel> DiPoints { get; } = new();

    /// <summary>输出点位（强制输出后显示黄色边框）。</summary>
    public ObservableCollection<IoPointViewModel> DoPoints { get; } = new();

    /// <summary>强制状态下拉项。</summary>
    public IReadOnlyList<string> ForceStates { get; } = new[] { ForceOnText, ForceOffText };

    /// <summary>是否暂停自动刷新（暂停后画面冻结，便于观察瞬时状态）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshButtonText))]
    private bool _isRefreshPaused;

    /// <summary>当前用户是否有强制输出权限（仅工程师）。</summary>
    [ObservableProperty]
    private bool _canForceOutput;

    /// <summary>强制输出选中的点位。</summary>
    [ObservableProperty]
    private IoPointViewModel? _selectedDoPoint;

    /// <summary>强制输出选中的状态（ON / OFF）。</summary>
    [ObservableProperty]
    private string _selectedForceState = ForceOnText;

    /// <summary>是否虚拟硬件模式（真实模式不提供输入模拟）。</summary>
    public bool IsVirtualMode => _virtualIo is not null;

    /// <summary>输入模拟当前选中的点位（默认 X00 载具到位）。</summary>
    [ObservableProperty]
    private IoPointViewModel? _selectedDiPoint;

    /// <summary>操作结果提示（最近一次强制写入 / 清除 / 模拟的结果）。</summary>
    [ObservableProperty]
    private string _operationText = "";

    /// <summary>刷新按钮文字。</summary>
    public string RefreshButtonText => IsRefreshPaused ? "继续刷新" : "暂停刷新";

    /// <summary>进入本页：判断强制输出权限并启动 200ms 刷新。</summary>
    /// <param name="ct">取消令牌。</param>
    public override Task<bool> OnEnteringAsync(CancellationToken ct)
    {
        CanForceOutput = _auth.HasPermission(Permission.IoForce);
        _log.LogInformation("进入 IO 监控页：用户 {User}，强制输出权限 {CanForce}",
                            _auth.CurrentUser?.UserName, CanForceOutput);

        ApplySnapshot(_monitor.Current);
        StartRefreshLoop();
        return Task.FromResult(true);
    }

    /// <summary>离开本页：停止刷新并清除全部强制输出（08-页面清单 页面 7 交互规则）。</summary>
    /// <param name="ct">取消令牌。</param>
    public override Task OnLeaveAsync(CancellationToken ct)
    {
        StopRefreshLoop();
        ClearForcedCore("离开 IO 页");
        return Task.CompletedTask;
    }

    /// <summary>切换自动刷新 / 暂停。</summary>
    [RelayCommand]
    private void ToggleRefresh()
    {
        IsRefreshPaused = !IsRefreshPaused;
        _log.LogDebug("IO 页刷新状态：{State}", IsRefreshPaused ? "暂停" : "自动 200ms");
    }

    /// <summary>写入强制输出（仅工程师；ViewModel 层再次校验权限）。</summary>
    [RelayCommand(CanExecute = nameof(CanForceWrite))]
    private void ForceWrite()
    {
        if (!_auth.HasPermission(Permission.IoForce))
        {
            _log.LogWarning("越权尝试 IO 强制输出：用户 {User}", _auth.CurrentUser?.UserName);
            _notifier.Warn("IO 强制输出需要工程师权限，请切换到工程师账户后再试。");
            return;
        }

        var point = SelectedDoPoint;
        if (point is null)
        {
            return;
        }

        var on = string.Equals(SelectedForceState, ForceOnText, StringComparison.Ordinal);

        try
        {
            _io.ForceOutput(point.Point, on);
            point.IsForced = true;
            point.IsOn = on;
            MarkPending(point.Point, on);
            OperationText = $"已强制 {point.Point}（{point.DisplayName}）= {(on ? ForceOnText : ForceOffText)}";
            _log.LogWarning("IO 强制输出：{Point}（{Name}）= {State}，操作人 {User}",
                            point.Point, point.DisplayName, on ? ForceOnText : ForceOffText,
                            _auth.CurrentUser?.UserName);
        }
        catch (HardwareException ex)
        {
            _log.LogError(ex, "IO 强制输出失败：{Point}", point.Point);
            _notifier.Warn($"强制输出 {point.Point} 失败：{ex.Message}");
        }
    }

    /// <summary>清除全部强制输出。</summary>
    [RelayCommand]
    private void ClearForced() => ClearForcedCore("用户点击全部清除");

    /// <summary>模拟输入置 ON（仅虚拟模式 + 工程师权限）。用于虚拟模式下放行自动流程，如 X00 载具到位。</summary>
    [RelayCommand(CanExecute = nameof(CanSimulateDi))]
    private void SimulateDiOn() => SimulateDi(true);

    /// <summary>模拟输入置 OFF（仅虚拟模式 + 工程师权限）。</summary>
    [RelayCommand(CanExecute = nameof(CanSimulateDi))]
    private void SimulateDiOff() => SimulateDi(false);

    /// <inheritdoc />
    protected override void DisposeCore()
    {
        StopRefreshLoop();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private bool CanForceWrite() => CanForceOutput && SelectedDoPoint is not null;

    private bool CanSimulateDi() => IsVirtualMode && CanForceOutput && SelectedDiPoint is not null;

    /// <summary>
    /// 模拟一路输入信号。虚拟模式没有真实传感器，自动流程的「载具到位」「料盘有余料」等
    /// 触发条件只能由此手动给出；真实模式下 <see cref="IsVirtualMode"/> 为 false，命令始终不可用。
    /// 权限沿用 <see cref="Permission.IoForce"/>：伪造 X05/X06/X07 可绕过安全互锁，只能工程师操作。
    /// </summary>
    /// <param name="on">目标状态。</param>
    private void SimulateDi(bool on)
    {
        var point = SelectedDiPoint;
        if (_virtualIo is null || point is null)
        {
            return;
        }

        if (!_auth.HasPermission(Permission.IoForce))
        {
            _log.LogWarning("越权尝试模拟输入：用户 {User}", _auth.CurrentUser?.UserName);
            _notifier.Warn("模拟输入需要工程师权限，请切换到工程师账户后再试。");
            return;
        }

        _virtualIo.SetDi(point.Point, on);
        point.IsOn = on;
        MarkPending(point.Point, on);
        OperationText = $"已模拟 {point.Point}（{point.DisplayName}）= {(on ? ForceOnText : ForceOffText)}";
        _log.LogWarning("虚拟输入模拟：{Point}（{Name}）= {State}，操作人 {User}",
                        point.Point, point.DisplayName, on ? ForceOnText : ForceOffText,
                        _auth.CurrentUser?.UserName);
    }

    private void ClearForcedCore(string reason)
    {
        try
        {
            _io.ClearAllForcedOutput();

            foreach (var point in DoPoints)
            {
                point.IsForced = false;
            }

            lock (_pendingSync)
            {
                _pendingStates.Clear();
            }

            OperationText = "已清除全部强制输出";
            _log.LogInformation("清除全部 IO 强制输出（{Reason}）", reason);
        }
        catch (HardwareException ex)
        {
            _log.LogError(ex, "清除 IO 强制输出失败（{Reason}）", reason);
        }
    }

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
                if (IsRefreshPaused)
                {
                    continue;
                }

                var snapshot = _monitor.Current;
                await _ui.InvokeAsync(() => ApplySnapshot(snapshot)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 切页/退出时取消刷新属于正常路径，不记录异常堆栈以免刷满日志
            _log.LogDebug("IO 页刷新循环已停止");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "IO 页刷新循环异常终止，点位显示将停止刷新");
        }
    }

    /// <summary>把一帧快照写入界面（必须在 UI 线程调用）。</summary>
    private void ApplySnapshot(DeviceSnapshot snapshot)
    {
        foreach (var point in DiPoints)
        {
            // 刚点过的那一路：采集线程还没采到新值，用旧快照覆盖会让灯「亮—灭—亮」闪一下
            if (ShouldHold(point.Point, snapshot.Di(point.Point)))
            {
                continue;
            }

            point.Apply(snapshot.DiStates, null);
        }

        foreach (var point in DoPoints)
        {
            if (ShouldHold(point.Point, snapshot.Do(point.Point)))
            {
                continue;
            }

            point.Apply(snapshot.DoStates, snapshot.ForcedPoints);
        }
    }

    /// <summary>登记一次本地写入，随后最多 2 秒内不被旧快照覆盖。</summary>
    /// <param name="point">点位名。</param>
    /// <param name="value">刚写入的状态。</param>
    private void MarkPending(string point, bool value)
    {
        lock (_pendingSync)
        {
            _pendingStates[point] = new PendingState(value, Environment.TickCount64 + PendingHoldMs);
        }
    }

    /// <summary>
    /// 判断某个点位是否还应该保持本地显示。快照已追上目标值（或等待超时）时解除并返回
    /// <see langword="false"/>，交回快照驱动。
    /// </summary>
    /// <param name="point">点位名。</param>
    /// <param name="snapshotValue">当前快照中的值。</param>
    private bool ShouldHold(string point, bool snapshotValue)
    {
        lock (_pendingSync)
        {
            if (!_pendingStates.TryGetValue(point, out var pending))
            {
                return false;
            }

            if (snapshotValue == pending.Value || Environment.TickCount64 > pending.ExpireTick)
            {
                _pendingStates.Remove(point);
                return false;
            }

            return true;
        }
    }

    partial void OnCanForceOutputChanged(bool value)
    {
        ForceWriteCommand.NotifyCanExecuteChanged();
        SimulateDiOnCommand.NotifyCanExecuteChanged();
        SimulateDiOffCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedDoPointChanged(IoPointViewModel? value) => ForceWriteCommand.NotifyCanExecuteChanged();

    partial void OnSelectedDiPointChanged(IoPointViewModel? value)
    {
        SimulateDiOnCommand.NotifyCanExecuteChanged();
        SimulateDiOffCommand.NotifyCanExecuteChanged();
    }
}
