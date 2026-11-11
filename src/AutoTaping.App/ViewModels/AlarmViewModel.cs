// 职责：报警页 ViewModel——当前报警（确认 / 全部确认）、历史查询、导出 CSV、新报警蜂鸣与 30 分钟静音
// 层：App/ViewModels
// 依赖：IAlarmService/IAuthService/IIoService/IUserNotifier/IClock（Core/Abstractions）、
//       AlarmRecord/AlarmLevel/IoPointNames（Core/Models）、IUiDispatcher/IFilePicker（App/Infrastructure）、CommunityToolkit.Mvvm

using System.Collections.ObjectModel;
using System.Globalization;
using AutoTaping.App.Infrastructure;
using AutoTaping.App.Navigation;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AutoTaping.App.ViewModels;

/// <summary>
/// 报警页 ViewModel（08-页面清单 页面 5）。权限：全部角色可进并可确认单条，
/// 「全部确认」仅工程师（<see cref="Permission.AlarmAckAll"/>）。
/// 5 秒去重由 <see cref="IAlarmService"/> 负责（同代码 5 秒内重复触发不新增记录），本页只负责呈现。
/// </summary>
public sealed partial class AlarmViewModel : PageViewModel
{
    /// <summary>当前报警列表的刷新周期（ms）。报警是安全相关数据，1s 刷新足够且开销可控。</summary>
    private const int RefreshIntervalMs = 1_000;

    /// <summary>静音时长（分钟），08-页面清单 页面 5：蜂鸣器可静音 30 分钟。</summary>
    private const int MuteMinutes = 30;

    /// <summary>蜂鸣时长（ms）。</summary>
    private const int BeepDurationMs = 1_500;

    /// <summary>历史查询最大区间（天），08-页面清单 页面 6 的同类约束。</summary>
    private const int MaxQueryDays = 90;

    /// <summary>CSV 文件过滤器。</summary>
    private const string CsvFileFilter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*";

    private readonly IAlarmService _alarms;
    private readonly IAuthService _auth;
    private readonly IIoService _io;
    private readonly IUiDispatcher _ui;
    private readonly IUserNotifier _notifier;
    private readonly IFilePicker _filePicker;
    private readonly IClock _clock;
    private readonly ILogger<AlarmViewModel> _log;
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>已展示过的报警 Id（用于识别「新报警到达」并触发蜂鸣）。</summary>
    private readonly HashSet<int> _knownIds = new();

    private CancellationTokenSource? _refreshCts;
    private DateTime _muteUntil = DateTime.MinValue;

    /// <summary>构造报警页 ViewModel。</summary>
    /// <param name="alarms">报警服务（触发 / 去重 / 确认 / 恢复 / 导出）。</param>
    /// <param name="auth">认证与权限服务。</param>
    /// <param name="io">IO 服务（蜂鸣器 Y06）。</param>
    /// <param name="ui">UI 线程调度。</param>
    /// <param name="notifier">用户提示通道。</param>
    /// <param name="filePicker">文件选择（导出 CSV）。</param>
    /// <param name="clock">时钟（默认查询日期、静音计时）。</param>
    /// <param name="log">日志。</param>
    public AlarmViewModel(
        IAlarmService alarms,
        IAuthService auth,
        IIoService io,
        IUiDispatcher ui,
        IUserNotifier notifier,
        IFilePicker filePicker,
        IClock clock,
        ILogger<AlarmViewModel> log)
    {
        _alarms = alarms;
        _auth = auth;
        _io = io;
        _ui = ui;
        _notifier = notifier;
        _filePicker = filePicker;
        _clock = clock;
        _log = log;
    }

    /// <inheritdoc />
    public override PageKey Key => PageKey.Alarm;

    /// <inheritdoc />
    public override string Title => "报 警";

    /// <inheritdoc />
    public override string Icon => "⚠";

    /// <inheritdoc />
    public override Permission RequiredPermission => Permission.None;

    /// <summary>当前未确认且未恢复的报警。</summary>
    public ObservableCollection<AlarmRecord> ActiveAlarms { get; } = new();

    /// <summary>历史报警（按查询区间）。</summary>
    public ObservableCollection<AlarmRecord> HistoryAlarms { get; } = new();

    /// <summary>历史查询起始日期文本（yyyy-MM-dd）。</summary>
    [ObservableProperty]
    private string _fromDateText = "";

    /// <summary>历史查询结束日期文本（yyyy-MM-dd）。</summary>
    [ObservableProperty]
    private string _toDateText = "";

    /// <summary>当前用户是否可「全部确认」（仅工程师）。</summary>
    [ObservableProperty]
    private bool _canAckAll;

    /// <summary>是否处于静音状态。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteButtonText))]
    private bool _isMuted;

    /// <summary>当前报警条数文字（面板标题）。</summary>
    [ObservableProperty]
    private string _activeCountText = "当前未确认报警（0 条）";

    /// <summary>状态提示（最近一次操作结果 / 查询结果）。</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>静音按钮文字。</summary>
    public string MuteButtonText => IsMuted ? "取消静音" : "静音 30 分钟";

    /// <summary>进入本页：校验权限、设置默认查询日期、做一次查询并启动 1s 刷新。</summary>
    /// <param name="ct">取消令牌。</param>
    public override Task<bool> OnEnteringAsync(CancellationToken ct)
    {
        CanAckAll = _auth.HasPermission(Permission.AlarmAckAll);

        var today = _clock.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        FromDateText = today;
        ToDateText = today;

        _knownIds.Clear();
        RefreshActiveAlarms();
        QueryHistory();
        StartRefreshLoop();
        return Task.FromResult(true);
    }

    /// <summary>离开本页：停止刷新循环。</summary>
    /// <param name="ct">取消令牌。</param>
    public override Task OnLeaveAsync(CancellationToken ct)
    {
        StopRefreshLoop();
        return Task.CompletedTask;
    }

    /// <summary>确认单条报警（全部角色可执行）。</summary>
    /// <param name="record">待确认的报警。</param>
    [RelayCommand]
    private async Task AckAsync(AlarmRecord? record)
    {
        if (record is null)
        {
            return;
        }

        var user = _auth.CurrentUser?.UserName;
        if (string.IsNullOrEmpty(user))
        {
            _notifier.Warn("请先登录后再确认报警。");
            return;
        }

        try
        {
            await Task.Run(() => _alarms.Ack(record.Id, user));
            _log.LogInformation("确认报警 {Code}（Id={Id}），用户 {User}", record.Code, record.Id, user);
            StatusText = $"已确认 {record.Code}（{_clock.Now:HH:mm:ss}）";
            RefreshActiveAlarms();
            QueryHistory();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "确认报警失败：{Code}（Id={Id}）", record.Code, record.Id);
            _notifier.Error($"确认报警失败：{ex.GetBaseException().Message}。可能已被他人确认，请刷新后重试。");
        }
    }

    /// <summary>全部确认（仅工程师，二次确认）。</summary>
    [RelayCommand]
    private async Task AckAllAsync()
    {
        if (!_auth.HasPermission(Permission.AlarmAckAll))
        {
            _log.LogWarning("越权尝试「全部确认报警」：用户 {User}", _auth.CurrentUser?.UserName);
            _notifier.Warn("「全部确认」需要工程师权限，请切换到工程师账户后再试。");
            return;
        }

        if (ActiveAlarms.Count == 0)
        {
            _notifier.Info("当前没有未确认报警。");
            return;
        }

        if (!_notifier.Confirm("全部确认报警", $"即将确认当前 {ActiveAlarms.Count} 条报警，并清空当前报警列表。确认继续？"))
        {
            return;
        }

        var user = _auth.CurrentUser?.UserName;
        if (string.IsNullOrEmpty(user))
        {
            return;
        }

        try
        {
            var acked = await Task.Run(() => _alarms.AckAll(user));
            StatusText = $"已全部确认 {acked} 条报警";
            _notifier.Info($"已确认 {acked} 条报警。");
            RefreshActiveAlarms();
            QueryHistory();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "全部确认报警失败");
            _notifier.Error($"全部确认失败：{ex.GetBaseException().Message}。请检查数据库后重试。");
        }
    }

    /// <summary>按起止日期查询历史报警（最大 90 天）。</summary>
    [RelayCommand]
    private void QueryHistory()
    {
        if (!TryGetRange(out var from, out var to))
        {
            return;
        }

        try
        {
            var records = _alarms.QueryHistory(from, to);

            HistoryAlarms.Clear();
            foreach (var record in records)
            {
                HistoryAlarms.Add(record);
            }

            StatusText = $"历史报警 {records.Count} 条（{from:yyyy-MM-dd} ~ {to:yyyy-MM-dd}）";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "查询历史报警失败");
            _notifier.Error($"查询历史报警失败：{ex.GetBaseException().Message}。请稍后重试。");
        }
    }

    /// <summary>导出当前查询区间的报警记录为 CSV（UTF-8 with BOM）。</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (!TryGetRange(out var from, out var to))
        {
            return;
        }

        var defaultFileName = $"报警_{_clock.Now:yyyyMMdd_HHmmss}.csv";
        var path = _filePicker.PickSaveFile("导出报警记录", CsvFileFilter, defaultFileName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            await Task.Run(() => _alarms.ExportCsv(from, to, path));
            _log.LogInformation("导出报警 CSV：{Path}", path);
            StatusText = $"已导出到 {path}";
            _notifier.Info($"报警记录已导出到：{path}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "导出报警 CSV 失败：{Path}", path);
            _notifier.Error($"导出失败：{ex.GetBaseException().Message}。请确认目标目录可写。");
        }
    }

    /// <summary>切换静音（30 分钟）。</summary>
    [RelayCommand]
    private void ToggleMute()
    {
        if (IsMuted)
        {
            _muteUntil = DateTime.MinValue;
            IsMuted = false;
            _notifier.Info("已取消静音。");
            return;
        }

        _muteUntil = _clock.Now.AddMinutes(MuteMinutes);
        IsMuted = true;
        _notifier.Info($"已静音 {MuteMinutes} 分钟，报警仍会记录但不蜂鸣。");
    }

    /// <inheritdoc />
    protected override void DisposeCore()
    {
        StopRefreshLoop();
        _lifetime.Cancel();
        _lifetime.Dispose();
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
                // 读库放后台线程，界面赋值切回 UI 线程（01-架构设计 第 3 节）
                var active = TryReadActive();
                await _ui.InvokeAsync(() => ApplyActive(active)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _log.LogDebug("报警页刷新循环已停止");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "报警页刷新循环异常终止");
        }
    }

    private IReadOnlyList<AlarmRecord>? TryReadActive()
    {
        try
        {
            return _alarms.GetActiveAlarms();
        }
        catch (Exception ex)
        {
            // 单次读失败不终止循环，下个周期再试（01-架构设计 第 6 节：采集类线程不能死）
            _log.LogError(ex, "读取当前报警失败，本次刷新跳过");
            return null;
        }
    }

    /// <summary>把后台读到的当前报警写入界面（必须在 UI 线程调用）。</summary>
    private void ApplyActive(IReadOnlyList<AlarmRecord>? active)
    {
        if (active is null)
        {
            return;
        }

        ApplyMuteState();

        var changed = active.Count != ActiveAlarms.Count
                      || active.Where((record, index) => record.Id != ActiveAlarms[index].Id).Any();

        if (changed)
        {
            ActiveAlarms.Clear();
            foreach (var record in active)
            {
                ActiveAlarms.Add(record);
            }
        }

        ActiveCountText = $"当前未确认报警（{active.Count} 条）";
        NotifyNewAlarms(active);
    }

    /// <summary>识别新到达的报警：蜂鸣（受静音限制）。</summary>
    private void NotifyNewAlarms(IReadOnlyList<AlarmRecord> active)
    {
        if (_knownIds.Count == 0)
        {
            // 首次进入页面只登记，不为历史报警蜂鸣
            foreach (var record in active)
            {
                _knownIds.Add(record.Id);
            }

            return;
        }

        var isNew = false;
        foreach (var record in active)
        {
            if (_knownIds.Add(record.Id) && record.Level >= AlarmLevel.Warning)
            {
                isNew = true;
            }
        }

        if (!isNew || IsMuted)
        {
            return;
        }

        _ = BeepAsync();
    }

    private async Task BeepAsync()
    {
        try
        {
            _io.WriteDo(IoPointNames.Buzzer, true);
            await Task.Delay(BeepDurationMs).ConfigureAwait(false);
            _io.WriteDo(IoPointNames.Buzzer, false);
        }
        catch (Exception ex)
        {
            // 蜂鸣失败不影响报警记录与显示
            _log.LogWarning(ex, "蜂鸣器输出失败");
        }
    }

    private void ApplyMuteState()
    {
        if (!IsMuted)
        {
            return;
        }

        if (_clock.Now >= _muteUntil)
        {
            IsMuted = false;
        }
    }

    /// <summary>立即刷新当前报警列表（确认 / 全部确认后调用）。</summary>
    private void RefreshActiveAlarms()
    {
        var active = TryReadActive();
        if (active is null)
        {
            return;
        }

        ApplyActive(active);
    }

    /// <summary>解析起止日期；非法或区间过大时提示并返回 <see langword="false"/>。</summary>
    private bool TryGetRange(out DateTime from, out DateTime to)
    {
        from = default;
        to = default;

        if (!DateTime.TryParseExact(FromDateText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                    DateTimeStyles.None, out var fromDate))
        {
            _notifier.Warn("起始日期格式应为 yyyy-MM-dd，例如 2026-11-05。");
            return false;
        }

        if (!DateTime.TryParseExact(ToDateText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                    DateTimeStyles.None, out var toDate))
        {
            _notifier.Warn("结束日期格式应为 yyyy-MM-dd，例如 2026-11-05。");
            return false;
        }

        if (toDate < fromDate)
        {
            _notifier.Warn("结束日期不能早于起始日期。");
            return false;
        }

        if ((toDate - fromDate).TotalDays > MaxQueryDays)
        {
            _notifier.Warn($"查询范围最大 {MaxQueryDays} 天，请缩小范围。");
            return false;
        }

        from = fromDate;
        to = toDate.AddDays(1).AddSeconds(-1);
        return true;
    }
}
