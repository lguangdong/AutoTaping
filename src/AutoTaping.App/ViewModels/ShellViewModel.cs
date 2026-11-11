// 职责：主窗口壳的 ViewModel——状态栏数据聚合、导航与权限控制、操作条占位命令、自动锁屏联动
// 层：App/ViewModels
// 依赖：IAuthService、IRecipeRepository、IOutputRepository、IAlarmRepository、IClock、
//       IUiDispatcher、IUserNotifier、IdleMonitor、PageViewModel

using System.Globalization;
using AutoTaping.App.Infrastructure;
using AutoTaping.App.Navigation;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using AutoTaping.Core.Services;
using AutoTaping.Core.StateMachine;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AutoTaping.App.ViewModels;

/// <summary>
/// 主窗口壳 ViewModel。负责 07-UI设计规范 第 5.1 节的四个区域中除内容区本身之外的全部状态：
/// 状态栏数值（每秒刷新一次）、导航项与权限、操作条按钮（S6 才接流程，本阶段只提示）、
/// 以及无操作超时后的自动锁屏请求。
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    /// <summary>状态栏刷新周期（秒）。11-开发任务分解 S3 验收标准 2 要求时间每秒跳动。</summary>
    private const int StatusRefreshSeconds = 1;

    /// <summary>未选择配方时的显示文本（08-页面清单 页面 1 的空状态处理）。</summary>
    private const string NoRecipeText = "未选择配方";

    private readonly IAuthService _auth;
    private readonly IRecipeRepository _recipes;
    private readonly IOutputRepository _outputs;
    private readonly IAlarmRepository _alarms;
    private readonly IClock _clock;
    private readonly IUiDispatcher _ui;
    private readonly IUserNotifier _notifier;
    private readonly IdleMonitor _idleMonitor;
    private readonly IAutoFlowCoordinator _coordinator;
    private readonly ILogger<ShellViewModel> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly IReadOnlyList<NavigationItem> _navigationItems;
    private readonly PageViewModel _autoPage;
    private readonly PageViewModel _alarmPage;

    private bool _disposed;

    /// <summary>当前显示在内容区的页面。</summary>
    [ObservableProperty]
    private PageViewModel? _currentPage;

    /// <summary>
    /// 当前设备状态。S3 还没有状态机，固定为 <see cref="DeviceState.NotReady"/>；
    /// S6 接入 DeviceStateMachine 后由状态变更事件驱动。
    /// </summary>
    [ObservableProperty]
    private DeviceState _currentState = DeviceState.NotReady;

    /// <summary>状态灯旁的文字（07-UI设计规范 第 6 节）。</summary>
    [ObservableProperty]
    private string _deviceStateText = "未就绪";

    /// <summary>状态栏：当前配方名。</summary>
    [ObservableProperty]
    private string _recipeName = NoRecipeText;

    /// <summary>状态栏：当班产量。</summary>
    [ObservableProperty]
    private string _outputText = "--";

    /// <summary>状态栏：良率。</summary>
    [ObservableProperty]
    private string _yieldText = "--";

    /// <summary>状态栏：未确认报警数。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlarmBadgeText))]
    private int _unackedAlarmCount;

    /// <summary>状态栏：当前用户与角色。</summary>
    [ObservableProperty]
    private string _currentUserText = "未登录";

    /// <summary>状态栏：系统时间（每秒刷新）。</summary>
    [ObservableProperty]
    private string _clockText = "";

    /// <summary>操作条：平均节拍。</summary>
    [ObservableProperty]
    private string _cycleTimeText = "-- s";

    /// <summary>构造主窗口壳 ViewModel。</summary>
    /// <param name="auth">认证与权限服务。</param>
    /// <param name="recipes">配方仓储（状态栏显示当前配方）。</param>
    /// <param name="outputs">产量仓储（状态栏显示产量、良率、节拍）。</param>
    /// <param name="alarms">报警仓储（状态栏显示未确认报警数）。</param>
    /// <param name="clock">时钟（状态栏时间与当日统计区间）。</param>
    /// <param name="ui">UI 线程调度（后台刷新结果切回 UI 线程）。</param>
    /// <param name="notifier">用户提示通道（不得在 ViewModel 里直接弹 MessageBox）。</param>
    /// <param name="idleMonitor">无操作监测（自动锁屏）。</param>
    /// <param name="pages">全部页面 ViewModel（按 <see cref="PageKey"/> 排序后生成导航）。</param>
    /// <param name="log">日志。</param>
    public ShellViewModel(
        IAuthService auth,
        IRecipeRepository recipes,
        IOutputRepository outputs,
        IAlarmRepository alarms,
        IClock clock,
        IUiDispatcher ui,
        IUserNotifier notifier,
        IdleMonitor idleMonitor,
        IAutoFlowCoordinator coordinator,
        IEnumerable<PageViewModel> pages,
        ILogger<ShellViewModel> log)
    {
        _auth = auth;
        _recipes = recipes;
        _outputs = outputs;
        _alarms = alarms;
        _clock = clock;
        _ui = ui;
        _notifier = notifier;
        _idleMonitor = idleMonitor;
        _coordinator = coordinator;
        _log = log;

        var ordered = pages.OrderBy(page => (int)page.Key).ToList();
        _navigationItems = ordered.Select(page => new NavigationItem(page, SelectPageAsync)).ToList();
        _autoPage = ordered.First(page => page.Key == PageKey.Auto);
        _alarmPage = ordered.First(page => page.Key == PageKey.Alarm);

        // 页面内部（如手动页的「退出手动」）只发跳转请求，由壳统一执行切页
        foreach (var page in ordered)
        {
            page.NavigationRequested += OnNavigationRequested;
        }

        MoveTo(_autoPage);

        // 启动时主画面是直接显示的、不经过导航按钮，必须补一次「进入」生命周期，
        // 否则 AutoPage 的 200ms 刷新循环不会启动（表现为主画面永远停在初始值）。
        _ = EnterInitialPageAsync(_autoPage);

        _auth.CurrentUserChanged += OnCurrentUserChanged;
        _idleMonitor.IdleTimeout += OnIdleTimeout;
        _coordinator.StateChanged += OnCoordinatorStateChanged;

        ApplyUserState();
        ApplyCoordinatorState();

        _ = Task.Run(() => RefreshLoopAsync(_cts.Token));
    }

    /// <summary>导航项（顺序即 <see cref="PageKey"/> 顺序）。</summary>
    public IReadOnlyList<NavigationItem> NavigationItems => _navigationItems;

    /// <summary>状态栏报警徽标文字（0 条时显示"无报警"，避免红底 0 的视觉噪音）。</summary>
    public string AlarmBadgeText => UnackedAlarmCount == 0 ? "无报警" : $"⚠ {UnackedAlarmCount}";

    /// <summary>需要回到登录页（无操作超时）。由 App 负责显示登录框，ViewModel 不创建窗口。</summary>
    public event EventHandler? LockRequested;

    /// <summary>处理 IdleMonitor 的无操作超时：运行中不锁屏，否则登出并请求回登录页。</summary>
    private void OnIdleTimeout(object? sender, EventArgs e)
    {
        if (CurrentState is DeviceState.Running or DeviceState.Paused)
        {
            _log.LogDebug("设备运行中，跳过自动锁屏");
            return;
        }

        if (!_auth.IsAuthenticated)
        {
            return;
        }

        _log.LogInformation("无操作超时，自动锁定：用户 {UserName} 回到登录页", _auth.CurrentUser?.UserName);
        _auth.Logout();
        LockRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCurrentUserChanged(object? sender, EventArgs e) => ApplyUserState();

    /// <summary>按当前用户刷新状态栏用户栏与导航可用状态；权限丢失时退回主画面。</summary>
    private void ApplyUserState()
    {
        var user = _auth.CurrentUser;
        CurrentUserText = user is null ? "未登录" : $"{user.UserName}（{GetRoleName(user.Role)}）";

        foreach (var item in _navigationItems)
        {
            item.IsAllowed = _auth.HasPermission(item.Page.RequiredPermission);
        }

        if (CurrentPage is not null && !_auth.HasPermission(CurrentPage.RequiredPermission))
        {
            MoveTo(_autoPage);
        }

        // 登录 / 锁屏后重新计时，避免刚登录就被判为空闲
        _idleMonitor.Reset();
    }

    /// <summary>
    /// 导航点击：ViewModel 层二次校验权限（00-项目总览 第 5 节，防止界面置灰被绕过），
    /// 再让新页做进入确认（手动页二次确认）、旧页做离开清理（停点动 / 清强制输出 / 抬 Z 轴）。
    /// 新页拒绝进入时保持当前页不变。
    /// </summary>
    private async Task SelectPageAsync(PageViewModel page)
    {
        if (!_auth.IsAuthenticated)
        {
            _notifier.Warn("请先登录后再切换页面。");
            return;
        }

        if (!_auth.HasPermission(page.RequiredPermission))
        {
            var roleName = PermissionPolicy.GetRequiredRoleName(page.RequiredPermission);
            _notifier.Warn($"进入「{page.Title}」需要{roleName}权限，请切换到{roleName}账户后再试。");
            return;
        }

        if (ReferenceEquals(page, CurrentPage))
        {
            return;
        }

        try
        {
            if (!await page.OnEnteringAsync(_cts.Token))
            {
                _log.LogInformation("用户取消进入页面 {Page}，保持当前页", page.Title);
                return;
            }

            if (CurrentPage is { } previous)
            {
                await previous.OnLeaveAsync(_cts.Token);
            }

            MoveTo(page);
            _log.LogDebug("切换到页面 {Page}", page.Title);
        }
        catch (OperationCanceledException ex)
        {
            _log.LogDebug(ex, "切换页面 {Page} 被取消（应用正在退出）", page.Title);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "切换页面 {Page} 失败", page.Title);
            _notifier.Error($"切换页面失败：{ex.GetBaseException().Message}。可以重试。");
        }
    }

    /// <summary>页面内部请求跳转（如手动页「退出手动」）：走与导航按钮相同的校验与生命周期。</summary>
    private async void OnNavigationRequested(object? sender, PageKey target)
    {
        try
        {
            var page = _navigationItems.Select(item => item.Page).FirstOrDefault(item => item.Key == target);
            if (page is null)
            {
                _log.LogWarning("页面请求跳转到未知页面 {Key}，已忽略", target);
                return;
            }

            await SelectPageAsync(page);
        }
        catch (Exception ex)
        {
            // 事件处理器是唯一允许的 async void（10-编码约定 四），必须自行兜底
            _log.LogError(ex, "页面跳转请求失败：{Key}", target);
        }
    }

    /// <summary>
    /// 启动时进入默认页面（主画面）。与 <see cref="SelectPageAsync"/> 的区别是不做权限校验：
    /// 主画面权限为 <see cref="Permission.None"/>，全部角色都能进。
    /// </summary>
    private async Task EnterInitialPageAsync(PageViewModel page)
    {
        try
        {
            if (!await page.OnEnteringAsync(_cts.Token).ConfigureAwait(true))
            {
                _log.LogWarning("初始页面 {Page} 拒绝进入，保持空白内容区", page.Title);
            }
        }
        catch (OperationCanceledException ex)
        {
            _log.LogDebug(ex, "进入初始页面 {Page} 被取消", page.Title);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "进入初始页面 {Page} 失败", page.Title);
        }
    }

    /// <summary>切换当前页并同步导航高亮。</summary>
    private void MoveTo(PageViewModel page)
    {
        CurrentPage = page;

        foreach (var item in _navigationItems)
        {
            item.IsSelected = ReferenceEquals(item.Page, page);
        }
    }

    /// <summary>操作条「启动」：Ready 下发车、Paused 下继续（由协调器内部区分）。</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start() => _coordinator.Start();

    /// <summary>操作条「暂停」：Running → Paused。</summary>
    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause() => _coordinator.Pause();

    /// <summary>操作条「停止」：Running / Paused → Ready。</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _coordinator.Stop();

    /// <summary>操作条「复位」：按当前状态执行（Alarming 确认后复位 / EStopped 释放后 ResetAxis / NotReady 回零）。</summary>
    [RelayCommand(CanExecute = nameof(CanReset))]
    private void Reset() => _coordinator.Reset();

    /// <summary>操作条「急停 / 释放」：急停态下点击释放急停，否则触发急停。</summary>
    [RelayCommand(CanExecute = nameof(CanEmergencyStop))]
    private void EmergencyStop()
    {
        if (_coordinator.State == DeviceState.EStopped)
        {
            _coordinator.ReleaseEmergencyStop();
        }
        else
        {
            _coordinator.EmergencyStop();
        }

        // 释放急停不改变设备状态（仍为 EStopped，直到点复位），不会触发 StateChanged，
        // 必须在此手动同步按钮文字与可用状态，否则界面看不出释放已生效。
        ApplyCoordinatorState();
    }

    /// <summary>单步模式开关文字。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SingleStepText))]
    private bool _isSingleStep;

    /// <summary>单步模式按钮文字。</summary>
    public string SingleStepText => IsSingleStep ? "单步：开" : "单步";

    /// <summary>切换单步模式（技术员以上权限，05 第 4.3 节）。</summary>
    [RelayCommand]
    private void ToggleSingleStep()
    {
        if (!_auth.HasPermission(Permission.ManualMode))
        {
            _notifier.Warn("单步模式需要技术员权限。");
            return;
        }

        _coordinator.SetSingleStep(!_coordinator.IsSingleStep);
        IsSingleStep = _coordinator.IsSingleStep;
        _notifier.Info(IsSingleStep
            ? "已开启单步模式：每次点启动只走一步（现场调试用）。"
            : "已关闭单步模式。");
    }

    /// <summary>状态栏报警徽标：跳转报警页（生成的命令名为 <c>ShowAlarmsCommand</c>）。</summary>
    [RelayCommand]
    private Task ShowAlarmsAsync() => SelectPageAsync(_alarmPage);

    /// <summary>是否处于急停态（驱动急停按钮文字与急停状态灯）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmergencyStopText), nameof(EmergencyStopStatusText))]
    private bool _isEStopped;

    /// <summary>急停是否已被释放（急停态下点过「释放急停」后为 true，复位后随状态变化重新同步）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmergencyStopText), nameof(EmergencyStopStatusText))]
    [NotifyCanExecuteChangedFor(nameof(EmergencyStopCommand))]
    private bool _isEStopReleased;

    /// <summary>
    /// 急停按钮文字。急停态下：未释放显示「释放急停」（可点），已释放显示「急停已释放」
    /// （按钮置灰，提示下一步点复位）；非急停态显示「急停」。
    /// </summary>
    public string EmergencyStopText => IsEStopped
        ? (IsEStopReleased ? "急停已释放" : "释放急停")
        : "急停";

    /// <summary>急停状态灯文字。</summary>
    public string EmergencyStopStatusText => IsEStopped
        ? (IsEStopReleased ? "急停：已释放，待复位" : "急停：已触发")
        : "急停：正常";

    private bool CanStart() => CurrentState is DeviceState.Ready or DeviceState.Paused;

    private bool CanPause() => CurrentState is DeviceState.Running;

    private bool CanStop() => CurrentState is DeviceState.Running or DeviceState.Paused;

    private bool CanReset() => CurrentState is DeviceState.Alarming or DeviceState.EStopped or DeviceState.NotReady;

    /// <summary>
    /// 急停按钮可用性：急停态下按钮变成「释放急停」，必须保持可点（释放过一次后置灰等复位）；
    /// 其余状态（就绪 / 运行 / 暂停 / 报警 / 未就绪）都允许随时触发急停，仅退出中禁用。
    /// </summary>
    private bool CanEmergencyStop() => CurrentState == DeviceState.EStopped
        ? !IsEStopReleased
        : CurrentState != DeviceState.Exiting;

    /// <summary>协调器状态变更（可能在后台线程触发），切回 UI 线程刷新状态栏与操作条。</summary>
    private void OnCoordinatorStateChanged(object? sender, EventArgs e)
    {
        _ui.InvokeAsync(ApplyCoordinatorState);
    }

    /// <summary>把协调器状态写入状态栏与操作条（必须在 UI 线程调用）。</summary>
    private void ApplyCoordinatorState()
    {
        CurrentState = _coordinator.State;
        DeviceStateText = GetDeviceStateText(CurrentState);
        IsEStopped = CurrentState == DeviceState.EStopped;

        // 释放标记存在协调器里，状态不变时也要跟着同步（释放后按钮文字/可用性依赖它）
        IsEStopReleased = _coordinator.IsEmergencyStopReleased;

        StartCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        EmergencyStopCommand.NotifyCanExecuteChanged();
    }

    private static string GetDeviceStateText(DeviceState state) => state switch
    {
        DeviceState.Initializing => "初始化中",
        DeviceState.NotReady => "未就绪",
        DeviceState.Ready => "就绪",
        DeviceState.Running => "运行中",
        DeviceState.Paused => "已暂停",
        DeviceState.Alarming => "故障",
        DeviceState.EStopped => "急停",
        DeviceState.Exiting => "退出中",
        _ => "未知"
    };

    /// <summary>
    /// 状态栏刷新循环。数据库读取与格式化在后台线程完成，结果统一切回 UI 线程赋值
    /// （01-架构设计 第 3 节：后台线程不得直接更新界面）。
    /// </summary>
    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(StatusRefreshSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var snapshot = TryReadSnapshot();
                await _ui.InvokeAsync(() => ApplySnapshot(snapshot)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex)
        {
            // 关闭窗口时的正常退出路径
            _log.LogDebug(ex, "状态栏刷新循环已停止");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "状态栏刷新循环异常终止，状态栏将停止刷新");
        }
    }

    /// <summary>读取一次状态栏数据。读库失败只跳过本次刷新，不能终止循环（01-架构设计 第 6 节）。</summary>
    private StatusSnapshot? TryReadSnapshot()
    {
        try
        {
            var now = _clock.Now;
            var recipe = _recipes.GetActive();
            var statistics = _outputs.GetStatistics(now.Date, now);
            var unacked = _alarms.CountUnacked((int)AlarmLevel.Info);

            return new StatusSnapshot(
                recipe?.Name ?? NoRecipeText,
                statistics.TotalCount,
                statistics.Yield,
                statistics.AverageCycleTimeMs,
                unacked,
                now);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "读取状态栏数据失败，本次刷新跳过");
            return null;
        }
    }

    /// <summary>把后台读到的快照写进绑定属性（必须在 UI 线程调用）。</summary>
    private void ApplySnapshot(StatusSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        RecipeName = snapshot.RecipeName;
        OutputText = $"{snapshot.TotalCount} 件";
        YieldText = snapshot.TotalCount == 0 ? "--" : $"{snapshot.Yield * 100:F1}%";
        CycleTimeText = snapshot.AverageCycleTimeMs <= 0 ? "-- s" : $"{snapshot.AverageCycleTimeMs / 1000.0:F2} s";
        UnackedAlarmCount = snapshot.UnackedAlarmCount;
        ClockText = snapshot.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string GetRoleName(UserRole role) => role switch
    {
        UserRole.Operator => "操作员",
        UserRole.Technician => "技术员",
        UserRole.Engineer => "工程师",
        _ => "未知角色"
    };

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _auth.CurrentUserChanged -= OnCurrentUserChanged;
        _idleMonitor.IdleTimeout -= OnIdleTimeout;
        _coordinator.StateChanged -= OnCoordinatorStateChanged;
        _cts.Cancel();
        _cts.Dispose();

        // 页面持有的采集循环必须随程序退出释放（10-编码约定 五）
        foreach (var item in _navigationItems)
        {
            item.Page.NavigationRequested -= OnNavigationRequested;
            item.Page.Dispose();
        }
    }

    /// <summary>状态栏一次刷新读到的数据（后台线程产出，UI 线程消费）。</summary>
    private sealed record StatusSnapshot(
        string RecipeName,
        int TotalCount,
        double Yield,
        double AverageCycleTimeMs,
        int UnackedAlarmCount,
        DateTime Now);
}
