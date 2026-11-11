// 职责：产量统计页 ViewModel——当日统计卡片、小时柱状图、停机分析、明细表与 CSV 导出
// 层：App/ViewModels
// 依赖：IOutputService/IAuthService/IUserNotifier/IClock（Core/Abstractions）、
//       OutputDaySummary/HourlyOutput/DowntimeSummary/OutputRecord/ShiftSchedule（Core/Models）、
//       IUiDispatcher/IFilePicker（App/Infrastructure）、CommunityToolkit.Mvvm

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
/// 产量统计页 ViewModel（08-页面清单 页面 6）。只读页面，无可编辑项；
/// 柱状图用矩形绘制，不引入第三方图表库。班次筛选只作用于明细表，统计卡片与柱状图为整日数据
/// （界面会明确提示，避免操作工误读）。
/// </summary>
public sealed partial class OutputViewModel : PageViewModel
{
    /// <summary>页面激活期间的自动刷新周期（ms）。</summary>
    private const int RefreshIntervalMs = 5_000;

    /// <summary>小时数（柱状图固定 24 根柱子）。</summary>
    private const int HoursPerDay = 24;

    /// <summary>明细表最多显示的条数（08-页面清单 页面 6：最近 100 条）。</summary>
    private const int DetailLimit = 100;

    /// <summary>CSV 文件过滤器。</summary>
    private const string CsvFileFilter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*";

    /// <summary>班次下拉的「全部」选项名。</summary>
    private const string AllShiftText = "全部班次";

    /// <summary>班次代码，与 <see cref="ShiftOptions"/> 下标一一对应（<see langword="null"/> = 全部）。</summary>
    private static readonly string?[] ShiftCodes = [null, ShiftSchedule.Day, ShiftSchedule.Swing, ShiftSchedule.Night];

    private readonly IOutputService _outputs;
    private readonly IAuthService _auth;
    private readonly IUiDispatcher _ui;
    private readonly IUserNotifier _notifier;
    private readonly IFilePicker _filePicker;
    private readonly IClock _clock;
    private readonly ILogger<OutputViewModel> _log;
    private readonly CancellationTokenSource _lifetime = new();

    private CancellationTokenSource? _refreshCts;

    /// <summary>构造产量统计页 ViewModel。</summary>
    /// <param name="outputs">产量统计服务。</param>
    /// <param name="auth">认证与权限服务（导出权限）。</param>
    /// <param name="ui">UI 线程调度。</param>
    /// <param name="notifier">用户提示通道。</param>
    /// <param name="filePicker">文件选择（导出 CSV）。</param>
    /// <param name="clock">时钟（默认日期）。</param>
    /// <param name="log">日志。</param>
    public OutputViewModel(
        IOutputService outputs,
        IAuthService auth,
        IUiDispatcher ui,
        IUserNotifier notifier,
        IFilePicker filePicker,
        IClock clock,
        ILogger<OutputViewModel> log)
    {
        _outputs = outputs;
        _auth = auth;
        _ui = ui;
        _notifier = notifier;
        _filePicker = filePicker;
        _clock = clock;
        _log = log;

        for (var hour = 0; hour < HoursPerDay; hour++)
        {
            HourlyBars.Add(new HourlyBarViewModel(hour));
        }
    }

    /// <inheritdoc />
    public override PageKey Key => PageKey.Output;

    /// <inheritdoc />
    public override string Title => "产量统计";

    /// <inheritdoc />
    public override string Icon => "▤";

    /// <inheritdoc />
    public override Permission RequiredPermission => Permission.None;

    /// <summary>小时柱状图的 24 根柱子（创建后只更新属性，不重建集合）。</summary>
    public ObservableCollection<HourlyBarViewModel> HourlyBars { get; } = new();

    /// <summary>停机分析（按原因汇总）。</summary>
    public ObservableCollection<DowntimeSummary> Downtimes { get; } = new();

    /// <summary>明细记录（最近 100 条）。</summary>
    public ObservableCollection<OutputRecord> Records { get; } = new();

    /// <summary>班次下拉选项（显示名，与 <see cref="ShiftCodes"/> 一一对应）。</summary>
    public IReadOnlyList<string> ShiftOptions { get; } =
    [
        AllShiftText,
        ShiftSchedule.GetDisplayName(ShiftSchedule.Day),
        ShiftSchedule.GetDisplayName(ShiftSchedule.Swing),
        ShiftSchedule.GetDisplayName(ShiftSchedule.Night)
    ];

    /// <summary>查询日期文本（yyyy-MM-dd）。</summary>
    [ObservableProperty]
    private string _dateText = "";

    /// <summary>当前班次筛选显示名（明细表用）。</summary>
    [ObservableProperty]
    private string _selectedShiftText = AllShiftText;

    /// <summary>统计卡片：总产量。</summary>
    [ObservableProperty]
    private string _totalText = "--";

    /// <summary>统计卡片：良率。</summary>
    [ObservableProperty]
    private string _yieldText = "--";

    /// <summary>统计卡片：平均节拍。</summary>
    [ObservableProperty]
    private string _cycleText = "--";

    /// <summary>统计卡片：运行时长。</summary>
    [ObservableProperty]
    private string _runTimeText = "--";

    /// <summary>停机时长合计文字（停机分析面板标题用）。</summary>
    [ObservableProperty]
    private string _downTimeText = "--";

    /// <summary>班次分解文字（如「夜班 0 / 白班 320 / 中班 180」）。</summary>
    [ObservableProperty]
    private string _shiftText = "";

    /// <summary>当前用户是否可导出（技术员及以上）。</summary>
    [ObservableProperty]
    private bool _canExport;

    /// <summary>状态提示。</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>进入本页：设置默认日期、启动 5s 自动刷新并立即查询一次。</summary>
    /// <param name="ct">取消令牌。</param>
    public override Task<bool> OnEnteringAsync(CancellationToken ct)
    {
        CanExport = _auth.HasPermission(Permission.OutputExport);

        if (string.IsNullOrWhiteSpace(DateText))
        {
            DateText = _clock.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (string.IsNullOrWhiteSpace(SelectedShiftText))
        {
            SelectedShiftText = AllShiftText;
        }

        StartRefreshLoop();
        _ = RefreshAsync();
        return Task.FromResult(true);
    }

    /// <summary>离开本页：停止刷新循环。</summary>
    /// <param name="ct">取消令牌。</param>
    public override Task OnLeaveAsync(CancellationToken ct)
    {
        StopRefreshLoop();
        return Task.CompletedTask;
    }

    /// <summary>「查询」：按当前日期与班次刷新页面数据。</summary>
    [RelayCommand]
    private Task QueryAsync() => RefreshAsync();

    /// <summary>「导出CSV」：导出当日全部产出明细（UTF-8 with BOM）。</summary>
    [RelayCommand(CanExecute = nameof(CanExportCsv))]
    private async Task ExportAsync()
    {
        if (!_auth.HasPermission(Permission.OutputExport))
        {
            _log.LogWarning("越权尝试导出产量明细：用户 {User}", _auth.CurrentUser?.UserName);
            _notifier.Warn("导出产量明细需要技术员权限，请切换到技术员或工程师账户。");
            return;
        }

        if (!TryGetDate(out var date))
        {
            return;
        }

        var defaultFileName = $"产量明细_{date:yyyyMMdd}.csv";
        var path = _filePicker.PickSaveFile("导出产量明细", CsvFileFilter, defaultFileName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var from = date;
            var to = date.AddDays(1).AddSeconds(-1);
            await Task.Run(() => _outputs.ExportCsv(from, to, path));
            _log.LogInformation("导出产量明细 CSV：{Path}", path);
            StatusText = $"已导出到 {path}";
            _notifier.Info($"产量明细已导出到：{path}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "导出产量明细失败：{Path}", path);
            _notifier.Error($"导出失败：{ex.GetBaseException().Message}。请确认目标目录可写。");
        }
    }

    partial void OnSelectedShiftTextChanged(string value) => _ = RefreshAsync();

    partial void OnCanExportChanged(bool value) => ExportCommand.NotifyCanExecuteChanged();

    /// <inheritdoc />
    protected override void DisposeCore()
    {
        StopRefreshLoop();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private bool CanExportCsv() => CanExport;

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
                var snapshot = ReadSnapshot();
                await _ui.InvokeAsync(() => Apply(snapshot)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _log.LogDebug("产量页刷新循环已停止");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "产量页刷新循环异常终止");
        }
    }

    /// <summary>手动 / 定时刷新一次（读库在后台线程，界面赋值回 UI 线程）。</summary>
    private async Task RefreshAsync()
    {
        if (!TryGetDate(out _))
        {
            return;
        }

        try
        {
            var snapshot = await Task.Run(ReadSnapshot);
            await _ui.InvokeAsync(() => Apply(snapshot));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "刷新产量统计失败");
            _notifier.Error($"读取产量统计失败：{ex.GetBaseException().Message}。请检查数据库后重试。");
        }
    }

    /// <summary>在后台线程读取一次页面数据。</summary>
    private Snapshot ReadSnapshot()
    {
        if (!TryGetDate(out var date))
        {
            return Snapshot.Empty;
        }

        var shift = GetSelectedShiftCode();
        var from = date;
        var to = date.AddDays(1).AddSeconds(-1);

        // 班次汇总表是查询时重算的缓存：只在看当天时刷新，避免查看历史日期时把班次数据改成"现在"的
        var now = _clock.Now;
        if (date.Date == now.Date)
        {
            _outputs.RefreshShiftProduction(now);
        }

        return new Snapshot
        {
            Date = date,
            Summary = _outputs.GetDaySummary(date),
            Hourly = _outputs.GetHourlyOutput(date),
            Downtime = _outputs.GetDowntimeSummary(from, to),
            Records = _outputs.GetRecords(from, to, shift, DetailLimit)
        };
    }

    /// <summary>把一次查询结果写入界面（必须在 UI 线程调用）。</summary>
    private void Apply(Snapshot snapshot)
    {
        if (snapshot.Date == default)
        {
            return;
        }

        var summary = snapshot.Summary;
        TotalText = $"{summary.TotalCount} 件";
        YieldText = summary.TotalCount == 0 ? "--" : $"{summary.Yield * 100:F1}%";
        CycleText = summary.AverageCycleTimeMs <= 0 ? "--" : $"{summary.AverageCycleTimeMs / 1000.0:F2} s";
        RunTimeText = FormatDuration(summary.RunSeconds);
        DownTimeText = $"停机合计 {FormatDuration(summary.DowntimeSeconds)}";

        ShiftText = string.Join(" / ", summary.Shifts.Select(shift =>
            $"{ShiftSchedule.GetDisplayName(shift.ShiftName)} {shift.TotalCount}"));

        var maxCount = snapshot.Hourly.Count == 0 ? 0 : snapshot.Hourly.Max(hour => hour.Count);
        for (var hour = 0; hour < HourlyBars.Count; hour++)
        {
            var count = hour < snapshot.Hourly.Count ? snapshot.Hourly[hour].Count : 0;
            HourlyBars[hour].Apply(count, maxCount);
        }

        Downtimes.Clear();
        foreach (var downtime in snapshot.Downtime)
        {
            Downtimes.Add(downtime);
        }

        Records.Clear();
        foreach (var record in snapshot.Records)
        {
            Records.Add(record);
        }

        StatusText = $"{snapshot.Date:yyyy-MM-dd} {SelectedShiftText}：产出 {summary.TotalCount} 件，明细 {snapshot.Records.Count} 条"
                     + $"（{_clock.Now:HH:mm:ss} 刷新）";
    }

    /// <summary>把班次下拉的显示名换算成班次代码（全部班次返回 <see langword="null"/>）。</summary>
    private string? GetSelectedShiftCode()
    {
        var index = ShiftOptions.ToList().IndexOf(SelectedShiftText);
        return index >= 0 && index < ShiftCodes.Length ? ShiftCodes[index] : null;
    }

    private static string FormatDuration(int seconds)
        => $"{seconds / 3600:D2}:{seconds % 3600 / 60:D2}:{seconds % 60:D2}";

    /// <summary>解析查询日期；非法时提示并返回 <see langword="false"/>。</summary>
    private bool TryGetDate(out DateTime date)
    {
        if (DateTime.TryParseExact(DateText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                  DateTimeStyles.None, out date))
        {
            return true;
        }

        date = default;
        _notifier.Warn("日期格式应为 yyyy-MM-dd，例如 2026-11-05。");
        return false;
    }

    /// <summary>一次查询结果（后台线程产出，UI 线程消费）。</summary>
    private sealed record Snapshot
    {
        /// <summary>空快照：解析日期失败时使用。</summary>
        public static Snapshot Empty { get; } = new();

        public DateTime Date { get; init; }

        public OutputDaySummary Summary { get; init; } = new();

        public IReadOnlyList<HourlyOutput> Hourly { get; init; } = Array.Empty<HourlyOutput>();

        public IReadOnlyList<DowntimeSummary> Downtime { get; init; } = Array.Empty<DowntimeSummary>();

        public IReadOnlyList<OutputRecord> Records { get; init; } = Array.Empty<OutputRecord>();
    }
}
