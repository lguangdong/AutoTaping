// 职责：自动运行页（主画面）ViewModel——200ms 轮询展示工位状态与流转箭头、实时曲线、2.5D 侧视示意图、
//       连续运行时长、节拍产量与通讯状态（08-页面清单 页面 1，本页只读）
// 层：App/ViewModels
// 依赖：IDeviceMonitor / IAutoFlowCoordinator / IRecipeRepository / IOutputRepository / IClock / IUiDispatcher /
//       IUserNotifier（Core/Abstractions）、DeviceState（Core/StateMachine）、DeviceSnapshot / IoPointNames（Core/Models）、
//       DiagramOptions（App/Infrastructure）、communityToolkit.Mvvm、WPF PointCollection（仅画折线用）

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AutoTaping.App.Infrastructure;
using AutoTaping.App.Navigation;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using AutoTaping.Core.StateMachine;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AutoTaping.App.ViewModels;

/// <summary>
/// 自动运行页 ViewModel（08-页面清单 页面 1）。主画面只读、无任何修改操作；
/// 刷新采用 200ms 轮询（采集线程快照 + 协调器状态），后台读、UI 线程赋值。
/// </summary>
public sealed partial class AutoViewModel : PageViewModel
{
    /// <summary>界面刷新周期（ms），与采集线程一致。</summary>
    private const int RefreshIntervalMs = 200;

    /// <summary>实时曲线保留的点数（08-页面清单 页面 1：最近 300 点，200ms/点约 1 分钟）。</summary>
    private const int HistoryLength = 300;

    /// <summary>曲线画布设计宽度（px）。XAML 里用 Viewbox 缩放，映射计算与实际像素无关。</summary>
    private const double CurveWidth = 300;

    /// <summary>曲线画布设计高度（px）。</summary>
    private const double CurveHeight = 100;

    /// <summary>贴胶头示意图宽度（px），与 AutoPage 里 Canvas 的贴胶头一致。</summary>
    private const double HeadWidth = 48;

    /// <summary>贴胶头在示意图里可移动的左右边界（px，两端各留 24 的余量）。</summary>
    private const double HeadLeftMin = 24;

    /// <summary>贴胶头左边界上限（px）= 画布宽 800 - 右侧余量 24 - 贴胶头宽度。</summary>
    private const double HeadLeftMax = 800 - 24 - HeadWidth;

    /// <summary>Z 轴最小时贴胶头的顶部坐标（贴近台面，px）。</summary>
    private const double HeadTopAtZMin = 120;

    /// <summary>Z 轴最大时贴胶头的顶部坐标（最高位，px）。</summary>
    private const double HeadTopAtZMax = 36;

    /// <summary>流程第 5 步是拍照识别（见 AutoFlowCoordinator.BuildSteps），用于记录最后拍照时间。</summary>
    private const int CaptureStepIndex = 5;

    private readonly IDeviceMonitor _monitor;
    private readonly IAutoFlowCoordinator _coordinator;
    private readonly IRecipeRepository _recipes;
    private readonly IOutputRepository _outputs;
    private readonly IClock _clock;
    private readonly IUiDispatcher _ui;
    private readonly IUserNotifier _notifier;
    private readonly DiagramOptions _diagram;
    private readonly ILogger<AutoViewModel> _log;
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>X 轴位置环形缓冲（mm）。</summary>
    private readonly double[] _xHistory = new double[HistoryLength];

    /// <summary>Z 轴位置环形缓冲（mm）。抽真空压力无模拟量通道（点表只有 X03 开关量），故第二条曲线取 Z 轴。</summary>
    private readonly double[] _zHistory = new double[HistoryLength];

    private CancellationTokenSource? _refreshCts;

    /// <summary>环形缓冲下一次写入位置。</summary>
    private int _historyHead;

    /// <summary>环形缓冲已累计的有效点数（≤ <see cref="HistoryLength"/>）。</summary>
    private int _historyCount;

    /// <summary>本次连续运行的起点；非运行状态为 <see langword="null"/>。</summary>
    private DateTime? _runStartedAt;

    /// <summary>最近一次进入拍照步骤的时刻（拍照是瞬时步骤，进入即视为刚拍完）。</summary>
    private DateTime? _lastCaptureAt;

    /// <summary>最近一帧快照（工位点击查看详情时用，避免在命令里再读一次采集快照）。</summary>
    private DeviceSnapshot _lastSnapshot = DeviceSnapshot.Empty;

    /// <summary>构造自动运行页 ViewModel。</summary>
    public AutoViewModel(
        IDeviceMonitor monitor,
        IAutoFlowCoordinator coordinator,
        IRecipeRepository recipes,
        IOutputRepository outputs,
        IClock clock,
        IUiDispatcher ui,
        IUserNotifier notifier,
        DiagramOptions diagram,
        ILogger<AutoViewModel> log)
    {
        _monitor = monitor;
        _coordinator = coordinator;
        _recipes = recipes;
        _outputs = outputs;
        _clock = clock;
        _ui = ui;
        _notifier = notifier;
        _diagram = diagram;
        _log = log;
    }

    /// <inheritdoc />
    public override PageKey Key => PageKey.Auto;

    /// <inheritdoc />
    public override string Title => "自动运行";

    /// <inheritdoc />
    public override string Icon => "▶";

    /// <inheritdoc />
    public override Permission RequiredPermission => Permission.None;

    /// <summary>当前设备状态（驱动横幅与工位状态）。</summary>
    [ObservableProperty]
    private DeviceState _state = DeviceState.NotReady;

    /// <summary>状态文字（未就绪 / 就绪 / 运行中 / 已暂停 / 故障 / 急停）。</summary>
    [ObservableProperty]
    private string _stateText = "未就绪";

    /// <summary>当前步骤文字（如 步骤 5/15 拍照识别）。</summary>
    [ObservableProperty]
    private string _stepText = "未运行";

    /// <summary>上料位是否为当前工位。</summary>
    [ObservableProperty]
    private bool _loadActive;

    /// <summary>拍照位是否为当前工位。</summary>
    [ObservableProperty]
    private bool _photoActive;

    /// <summary>贴附位是否为当前工位。</summary>
    [ObservableProperty]
    private bool _tapeActive;

    /// <summary>下料位是否为当前工位。</summary>
    [ObservableProperty]
    private bool _unloadActive;

    /// <summary>Z 轴位置文字。</summary>
    [ObservableProperty]
    private string _zText = "--";

    /// <summary>R 轴角度文字。</summary>
    [ObservableProperty]
    private string _rText = "--";

    /// <summary>真空状态文字（吸住 / 未吸住）。</summary>
    [ObservableProperty]
    private string _vacuumText = "--";

    /// <summary>气压状态文字（正常 / 不足）。</summary>
    [ObservableProperty]
    private string _pressureText = "--";

    /// <summary>相机是否连接。</summary>
    [ObservableProperty]
    private bool _cameraOk;

    /// <summary>运控卡是否连接。</summary>
    [ObservableProperty]
    private bool _motionOk;

    /// <summary>平均节拍文字。</summary>
    [ObservableProperty]
    private string _cycleTimeText = "--";

    /// <summary>当班产量文字。</summary>
    [ObservableProperty]
    private string _outputText = "--";

    /// <summary>良率文字。</summary>
    [ObservableProperty]
    private string _yieldText = "--";

    /// <summary>是否显示「未回零」横幅。</summary>
    [ObservableProperty]
    private bool _notHomed;

    /// <summary>是否显示「未选择配方」横幅。</summary>
    [ObservableProperty]
    private bool _noRecipe;

    // ==================== 工位状态（08-页面清单 页面 1：状态点 + 工位名 + 计数） ====================

    /// <summary>上料位有料（DI X00）。</summary>
    [ObservableProperty]
    private bool _loadHasMaterial;

    /// <summary>贴附位有料（DI X01）。</summary>
    [ObservableProperty]
    private bool _tapeHasMaterial;

    /// <summary>下料位有料（DI X02）。</summary>
    [ObservableProperty]
    private bool _unloadHasMaterial;

    /// <summary>真空吸住（DI X03）。</summary>
    [ObservableProperty]
    private bool _vacuumOn;

    /// <summary>上料位状态文字（有料 / 无料）。</summary>
    [ObservableProperty]
    private string _loadStateText = "无料";

    /// <summary>拍照位状态文字（相机就绪 / 相机未连接）。</summary>
    [ObservableProperty]
    private string _photoStateText = "相机未连接";

    /// <summary>贴附位状态文字（吸住 / 未吸住）。</summary>
    [ObservableProperty]
    private string _tapeStateText = "未吸住";

    /// <summary>下料位状态文字（有料 / 空闲）。</summary>
    [ObservableProperty]
    private string _unloadStateText = "空闲";

    /// <summary>上料 → 拍照 的连接箭头是否高亮（正在上料 / 移动到拍照位）。</summary>
    [ObservableProperty]
    private bool _arrow1Active;

    /// <summary>拍照 → 贴附 的连接箭头是否高亮（正在移动到贴附位 / Z 下压）。</summary>
    [ObservableProperty]
    private bool _arrow2Active;

    /// <summary>贴附 → 下料 的连接箭头是否高亮（Z 抬升 / 移动到下料位）。</summary>
    [ObservableProperty]
    private bool _arrow3Active;

    // ==================== 实时曲线（X 轴位置 / Z 轴位置） ====================

    /// <summary>X 轴位置曲线（最近 300 点，映射到 300×100 设计画布）。</summary>
    [ObservableProperty]
    private PointCollection _xCurvePoints = new();

    /// <summary>Z 轴位置曲线（最近 300 点，映射到 300×100 设计画布）。</summary>
    [ObservableProperty]
    private PointCollection _zCurvePoints = new();

    /// <summary>X 轴当前值（曲线标题右侧）。</summary>
    [ObservableProperty]
    private string _xCurveValueText = "-- mm";

    /// <summary>Z 轴当前值（曲线标题右侧）。</summary>
    [ObservableProperty]
    private string _zCurveValueText = "-- mm";

    // ==================== 连续运行与拍照时间 ====================

    /// <summary>本次连续运行时长（HH:mm:ss，暂停/停止后清零）。</summary>
    [ObservableProperty]
    private string _runTimeText = "00:00:00";

    /// <summary>最后拍照时间（HH:mm:ss，未拍过为 --）。</summary>
    [ObservableProperty]
    private string _lastCaptureText = "--";

    // ==================== 2.5D 侧视示意图（贴胶头位置，画布坐标 px） ====================

    /// <summary>贴胶头在示意图画布上的 Left（由 X 轴位置映射）。</summary>
    [ObservableProperty]
    private double _headCanvasLeft;

    /// <summary>贴胶头在示意图画布上的 Top（由 Z 轴位置映射，Z 越大越靠上）。</summary>
    [ObservableProperty]
    private double _headCanvasTop = HeadTopAtZMin;

    /// <summary>示意图右上角的 X/Y/Z 位置文字。</summary>
    [ObservableProperty]
    private string _headPositionText = "--";

    /// <inheritdoc />
    public override Task<bool> OnEnteringAsync(CancellationToken ct)
    {
        StartRefreshLoop();
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public override Task OnLeaveAsync(CancellationToken ct)
    {
        StopRefreshLoop();
        return Task.CompletedTask;
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
                var snapshot = _monitor.Current;
                var coordinatorState = _coordinator.State;
                await _ui.InvokeAsync(() => Apply(snapshot, coordinatorState)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _log.LogDebug("自动运行页刷新循环已停止");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "自动运行页刷新循环异常终止");
        }
    }

    /// <summary>把快照与协调器状态写入界面（必须在 UI 线程调用）。</summary>
    private void Apply(DeviceSnapshot snap, DeviceState state)
    {
        State = state;
        StateText = GetStateText(state);

        var stepIndex = _coordinator.CurrentStepIndex;
        StepText = stepIndex > 0
            ? $"步骤 {stepIndex}/{_coordinator.TotalSteps} {_coordinator.CurrentStepName}"
            : "未运行";

        LoadActive = IsInRange(stepIndex, 2, 2);
        PhotoActive = IsInRange(stepIndex, 3, 5);
        TapeActive = IsInRange(stepIndex, 6, 13);
        UnloadActive = IsInRange(stepIndex, 14, 15);

        // 箭头按「正在流转的那一段」高亮：上料→拍照、拍照→贴附、贴附→下料
        Arrow1Active = IsInRange(stepIndex, 2, 3);
        Arrow2Active = IsInRange(stepIndex, 6, 7);
        Arrow3Active = IsInRange(stepIndex, 13, 14);

        var x = snap.GetPosition(AxisId.X);
        var z = snap.GetPosition(AxisId.Z);

        ZText = $"{z:F2} mm";
        RText = $"{snap.GetPosition(AxisId.R):F2}°";
        VacuumOn = snap.Di(IoPointNames.VacuumSensor);
        VacuumText = VacuumOn ? "吸住" : "未吸住";
        PressureText = snap.Di(IoPointNames.AirPressureOk) ? "正常" : "不足";

        CameraOk = snap.CameraConnected;
        MotionOk = snap.MotionConnected;

        LoadHasMaterial = snap.Di(IoPointNames.MaterialAtLoad);
        TapeHasMaterial = snap.Di(IoPointNames.MaterialAtTape);
        UnloadHasMaterial = snap.Di(IoPointNames.MaterialAtUnload);
        LoadStateText = LoadHasMaterial ? "有料" : "无料";
        PhotoStateText = snap.CameraConnected ? "相机就绪" : "相机未连接";
        TapeStateText = VacuumOn ? "吸住" : "未吸住";
        UnloadStateText = UnloadHasMaterial ? "有料" : "空闲";

        NotHomed = !AllAxesHomed(snap);
        NoRecipe = _recipes.GetActive() is null;

        var now = _clock.Now;
        UpdateRunTime(state, now);
        UpdateLastCapture(state, stepIndex, now);
        UpdateCurves(x, z);
        UpdateDiagram(x, z, snap);

        _lastSnapshot = snap;

        var statistics = _outputs.GetStatistics(now.Date, now);
        CycleTimeText = statistics.AverageCycleTimeMs <= 0 ? "--" : $"{statistics.AverageCycleTimeMs / 1000.0:F2} s";
        OutputText = $"{statistics.TotalCount} 件";
        YieldText = statistics.TotalCount == 0 ? "--" : $"{statistics.Yield * 100:F1}%";
    }

    /// <summary>连续运行时长：进入 Running 开始计时，暂停 / 停止 / 故障即结束本次计时。</summary>
    private void UpdateRunTime(DeviceState state, DateTime now)
    {
        if (state != DeviceState.Running)
        {
            _runStartedAt = null;
            RunTimeText = "00:00:00";
            return;
        }

        _runStartedAt ??= now;
        RunTimeText = FormatDuration(now - _runStartedAt.Value);
    }

    /// <summary>最后拍照时间：拍照是流程第 5 步（瞬时步骤），进入该步骤即记录时刻。</summary>
    private void UpdateLastCapture(DeviceState state, int stepIndex, DateTime now)
    {
        if (state == DeviceState.Running && stepIndex == CaptureStepIndex)
        {
            _lastCaptureAt = now;
        }

        LastCaptureText = _lastCaptureAt?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "--";
    }

    /// <summary>把当前 X / Z 位置压入环形缓冲并重算两条折线。</summary>
    private void UpdateCurves(double x, double z)
    {
        _xHistory[_historyHead] = x;
        _zHistory[_historyHead] = z;
        _historyHead = (_historyHead + 1) % HistoryLength;
        if (_historyCount < HistoryLength)
        {
            _historyCount++;
        }

        XCurvePoints = BuildCurve(_xHistory, _diagram.XMinLimit, _diagram.XMaxLimit);
        ZCurvePoints = BuildCurve(_zHistory, _diagram.ZMinLimit, _diagram.ZMaxLimit);
        XCurveValueText = $"{x:F1} mm";
        ZCurveValueText = $"{z:F1} mm";
    }

    /// <summary>按软限位把环形缓冲映射成折线点（横轴固定 300 点，未填满时从左侧开始画）。</summary>
    private PointCollection BuildCurve(double[] buffer, double min, double max)
    {
        var points = new PointCollection(_historyCount);
        var span = max - min;
        if (span <= 0)
        {
            span = 1;
        }

        var start = (_historyHead - _historyCount + HistoryLength) % HistoryLength;
        for (var i = 0; i < _historyCount; i++)
        {
            var value = buffer[(start + i) % HistoryLength];
            var normalized = Math.Clamp((value - min) / span, 0, 1);
            var px = i * CurveWidth / (HistoryLength - 1);
            var py = CurveHeight - (normalized * CurveHeight);
            points.Add(new Point(px, py));
        }

        return points;
    }

    /// <summary>2.5D 示意图：X 轴位置映射成左右位移，Z 轴位置映射成上下位移。</summary>
    private void UpdateDiagram(double x, double z, DeviceSnapshot snap)
    {
        HeadCanvasLeft = MapRange(x, _diagram.XMinLimit, _diagram.XMaxLimit, HeadLeftMin, HeadLeftMax);
        HeadCanvasTop = MapRange(z, _diagram.ZMinLimit, _diagram.ZMaxLimit, HeadTopAtZMin, HeadTopAtZMax);
        HeadPositionText = $"X {x:F1}　Y {snap.GetPosition(AxisId.Y):F1}　Z {z:F1} mm";
    }

    /// <summary>点击工位查看详情（08-页面清单 页面 1 交互规则：当前位置、IO 状态、最近拍照）。</summary>
    /// <param name="station">工位标识：load / photo / tape / unload。</param>
    [RelayCommand]
    private void ShowStationDetail(string? station)
    {
        var snap = _lastSnapshot;
        var stepIndex = _coordinator.CurrentStepIndex;
        var title = station switch
        {
            "load" => "上料位",
            "photo" => "拍照位",
            "tape" => "贴附位",
            "unload" => "下料位",
            _ => "工位"
        };

        var detail = station switch
        {
            "load" => $"有料：{(snap.Di(IoPointNames.MaterialAtLoad) ? "是" : "否")}\n"
                      + $"X 位置：{snap.GetPosition(AxisId.X):F2} mm",
            "photo" => $"相机：{(snap.CameraConnected ? "已连接" : "未连接")}\n"
                       + $"最后拍照：{LastCaptureText}",
            "tape" => $"真空：{TapeStateText}\n"
                      + $"Z 位置：{snap.GetPosition(AxisId.Z):F2} mm\n"
                      + $"R 角度：{snap.GetPosition(AxisId.R):F2}°\n"
                      + $"气压：{PressureText}",
            "unload" => $"有料：{(snap.Di(IoPointNames.MaterialAtUnload) ? "是" : "否")}\n"
                        + $"当班产量：{OutputText}",
            _ => "无数据"
        };

        var stepText = stepIndex > 0
            ? $"步骤 {stepIndex}/{_coordinator.TotalSteps} {_coordinator.CurrentStepName}"
            : "未运行";

        _notifier.Info($"{title}\n{detail}\n当前：{stepText}\n设备状态：{StateText}");
    }

    /// <summary>把数值从 [fromMin, fromMax] 线性映射到 [toMin, toMax]，超范围按边界截断。</summary>
    private static double MapRange(double value, double fromMin, double fromMax, double toMin, double toMax)
    {
        if (fromMax <= fromMin)
        {
            return toMin;
        }

        var normalized = Math.Clamp((value - fromMin) / (fromMax - fromMin), 0, 1);
        return toMin + (normalized * (toMax - toMin));
    }

    /// <summary>时长格式化为 HH:mm:ss（超过 24 小时继续累加小时数）。</summary>
    private static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return $"{(int)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}";
    }

    private static bool IsInRange(int stepIndex, int from, int to) => stepIndex >= from && stepIndex <= to;

    private static bool AllAxesHomed(DeviceSnapshot snap)
        => snap.IsHomed(AxisId.X) && snap.IsHomed(AxisId.Y) && snap.IsHomed(AxisId.Z) && snap.IsHomed(AxisId.R);

    private static string GetStateText(DeviceState state) => state switch
    {
        DeviceState.Initializing => "初始化中",
        DeviceState.NotReady => "未就绪",
        DeviceState.Ready => "就绪",
        DeviceState.Running => "运行中",
        DeviceState.Paused => "已暂停",
        DeviceState.Alarming => "故障",
        DeviceState.EStopped => "急停",
        _ => "未知"
    };
}
