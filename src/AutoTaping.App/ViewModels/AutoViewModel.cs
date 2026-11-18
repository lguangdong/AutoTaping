// 职责：自动运行页（主画面）ViewModel——200ms 轮询展示工位状态与流转箭头、四轴实时位置、实时曲线、
//       连续运行时长、节拍产量与通讯状态（08-页面清单 页面 1，本页只读）
// 层：App/ViewModels
// 依赖：IDeviceMonitor / IAutoFlowCoordinator / IRecipeRepository / IOutputRepository / IClock / IUiDispatcher /
//       IUserNotifier（Core/Abstractions）、DeviceState（Core/StateMachine）、DeviceSnapshot / IoPointNames（Core/Models）、
//       DiagramOptions（App/Infrastructure）、communityToolkit.Mvvm、WPF PointCollection（仅画折线用）

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    /// <summary>生产统计刷新周期（ms）。统计走数据库查询，不需要跟 200ms 的采集同频。</summary>
    private const int StatsRefreshMs = 2_000;

    /// <summary>缩略图显示尺寸（与 AutoPage 里的 Border 一致，5:4）。</summary>
    private const double ThumbWidth = 240;

    /// <summary>缩略图显示高度。</summary>
    private const double ThumbHeight = 192;

    /// <summary>MARK 标注圆点半径（px），用于把中心点换算成左上角。</summary>
    private const double MarkRadius = 5;

    /// <summary>分数趋势保留的拍数。</summary>
    private const int ScoreTrendLength = 30;

    /// <summary>分数趋势画布高度（px），与 XAML 中 Polyline 容器一致。</summary>
    private const double ScoreTrendHeight = 48;

    /// <summary>流程第 6 步是拍照识别（见 AutoFlowCoordinator.BuildSteps），用于记录最后拍照时间。</summary>
    private const int CaptureStepIndex = 6;

    private readonly IDeviceMonitor _monitor;
    private readonly IAutoFlowCoordinator _coordinator;
    private readonly IRecipeRepository _recipes;
    private readonly IOutputService _outputService;
    private readonly ProductionOptions _production;
    private readonly IClock _clock;
    private readonly IUiDispatcher _ui;
    private readonly IUserNotifier _notifier;
    private readonly ILogger<AutoViewModel> _log;
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>每拍的 MARK 较低分环形缓冲（用于趋势）。</summary>
    private readonly double[] _scoreHistory = new double[ScoreTrendLength];

    /// <summary>分数趋势环形缓冲写入位置。</summary>
    private int _scoreHead;

    /// <summary>分数趋势已累计点数。</summary>
    private int _scoreCount;

    private CancellationTokenSource? _refreshCts;

    /// <summary>上次刷新生产统计的时刻（<see cref="Environment.TickCount64"/>）。</summary>
    private long _lastStatsTick;

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
        IOutputService outputService,
        IClock clock,
        IUiDispatcher ui,
        IUserNotifier notifier,
        DiagramOptions diagram,
        ProductionOptions production,
        ILogger<AutoViewModel> log)
    {
        _monitor = monitor;
        _coordinator = coordinator;
        _recipes = recipes;
        _outputService = outputService;
        _production = production;
        _clock = clock;
        _ui = ui;
        _notifier = notifier;
        _log = log;

        Axes = new[]
        {
            new AxisReadout(AxisId.X, diagram.MinLimits[(int)AxisId.X], diagram.MaxLimits[(int)AxisId.X]),
            new AxisReadout(AxisId.Y, diagram.MinLimits[(int)AxisId.Y], diagram.MaxLimits[(int)AxisId.Y]),
            new AxisReadout(AxisId.Z, diagram.MinLimits[(int)AxisId.Z], diagram.MaxLimits[(int)AxisId.Z]),
            new AxisReadout(AxisId.R, diagram.MinLimits[(int)AxisId.R], diagram.MaxLimits[(int)AxisId.R]),
        };
    }

    /// <inheritdoc />
    public override PageKey Key => PageKey.Auto;

    /// <inheritdoc />
    public override string Title => "自动运行";

    /// <inheritdoc />
    public override string Icon => "▶";

    /// <inheritdoc />
    public override Permission RequiredPermission => Permission.None;

    /// <summary>四轴实时读数（顺序 X/Y/Z/R），每 200ms 刷新数值与行程占比。</summary>
    public IReadOnlyList<AxisReadout> Axes { get; }

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

    /// <summary>当前班次名（DAY / SWING / NIGHT）。</summary>
    [ObservableProperty]
    private string _shiftNameText = "--";

    /// <summary>本班产量 / 目标文字。</summary>
    [ObservableProperty]
    private string _shiftOutputText = "--";

    /// <summary>班次目标达成率（0~100，驱动进度条）。</summary>
    [ObservableProperty]
    private double _shiftProgress;

    /// <summary>达成率文字。</summary>
    [ObservableProperty]
    private string _shiftProgressText = "--";

    /// <summary>标准节拍提示文字（未配置时为空）。</summary>
    [ObservableProperty]
    private string _standardCycleText = "";

    /// <summary>实际 UPH（件/小时，含停机）。</summary>
    [ObservableProperty]
    private string _uphText = "--";

    /// <summary>可用率文字（运行时长 ÷ 总时长）。</summary>
    [ObservableProperty]
    private string _availabilityText = "可用率 --";

    /// <summary>当班停机时长文字。</summary>
    [ObservableProperty]
    private string _downtimeText = "--";

    /// <summary>当班停机占比最高的原因。</summary>
    [ObservableProperty]
    private string _downtimeTopText = "--";

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

    // ==================== 连续运行与拍照时间 ====================

    /// <summary>本次连续运行时长（HH:mm:ss，暂停/停止后清零）。</summary>
    [ObservableProperty]
    private string _runTimeText = "00:00:00";

    /// <summary>最后拍照时间（HH:mm:ss，未拍过为 --）。</summary>
    [ObservableProperty]
    private string _lastCaptureText = "--";

    // ==================== 视觉结果（最近一次拍照识别） ====================

    /// <summary>最近一次 ΔX 纠偏量。</summary>
    [ObservableProperty]
    private string _visionDeltaXText = "--";

    /// <summary>最近一次 ΔY 纠偏量。</summary>
    [ObservableProperty]
    private string _visionDeltaYText = "--";

    /// <summary>最近一次 Δθ 纠偏量。</summary>
    [ObservableProperty]
    private string _visionDeltaThetaText = "--";

    /// <summary>最近一次两个 MARK 的匹配分数。</summary>
    [ObservableProperty]
    private string _visionScoreText = "--";

    /// <summary>最近一次视觉结果说明（识别正常 / 失败原因）。</summary>
    [ObservableProperty]
    private string _visionResultText = "尚未拍照";

    /// <summary>最近一次视觉是否成功（驱动结果文字颜色）。</summary>
    [ObservableProperty]
    private bool _visionOk;

    /// <summary>最近一帧视觉缩略图（Gray8 → BitmapSource，仅在帧版本变化时重建）。</summary>
    [ObservableProperty]
    private ImageSource? _visionImage;

    /// <summary>识别耗时文字（如 <c>42 ms</c>）。</summary>
    [ObservableProperty]
    private string _visionTimeText = "--";

    /// <summary>是否在缩略图上显示两个 MARK 的标注。</summary>
    [ObservableProperty]
    private bool _showMarks;

    /// <summary>MARK1 标注在缩略图上的左上角 X。</summary>
    [ObservableProperty]
    private double _mark1CanvasLeft;

    /// <summary>MARK1 标注在缩略图上的左上角 Y。</summary>
    [ObservableProperty]
    private double _mark1CanvasTop;

    /// <summary>MARK2 标注在缩略图上的左上角 X。</summary>
    [ObservableProperty]
    private double _mark2CanvasLeft;

    /// <summary>MARK2 标注在缩略图上的左上角 Y。</summary>
    [ObservableProperty]
    private double _mark2CanvasTop;

    /// <summary>MARK 分数趋势折线（最近若干拍的较低分）。</summary>
    [ObservableProperty]
    private PointCollection _scoreTrendPoints = new();

    /// <summary>已转换过的帧版本号。</summary>
    private int _convertedFrameVersion = -1;

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
        TapeActive = IsInRange(stepIndex, 6, 10);
        UnloadActive = IsInRange(stepIndex, 11, 12);

        // 箭头按「正在流转的那一段」高亮：上料→拍照、拍照→贴附、贴附→下料
        Arrow1Active = IsInRange(stepIndex, 2, 3);
        Arrow2Active = IsInRange(stepIndex, 5, 6);
        Arrow3Active = IsInRange(stepIndex, 10, 11);

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
        UpdateAxes(snap);
        UpdateVision(_coordinator.LastVision);
        UpdateVisionFrame();

        _lastSnapshot = snap;

        // 生产统计走数据库，不需要 200ms 刷新一次；2 秒足以，且能把查询次数降一个数量级
        if (Environment.TickCount64 - _lastStatsTick >= StatsRefreshMs)
        {
            _lastStatsTick = Environment.TickCount64;
            UpdateStatistics(now);
        }
    }

    /// <summary>刷新当班统计（产量/目标、节拍、实际 UPH、停机与可用率）。</summary>
    /// <param name="now">当前时刻（决定日期与班次）。</param>
    private void UpdateStatistics(DateTime now)
    {
        var summary = _outputService.GetDaySummary(now.Date);

        OutputText = $"{summary.TotalCount} 件";
        CycleTimeText = summary.AverageCycleTimeMs <= 0
            ? "--"
            : $"{summary.AverageCycleTimeMs / 1000.0:F2} s";
        YieldText = summary.TotalCount == 0 ? "--" : $"{summary.Yield * 100:F1}%";

        // 本班产量 / 目标达成
        var shiftName = _outputService.GetShiftName(now);
        ShiftNameText = shiftName;

        var shiftCount = 0;
        foreach (var shift in summary.Shifts)
        {
            if (string.Equals(shift.ShiftName, shiftName, StringComparison.Ordinal))
            {
                shiftCount = shift.TotalCount;
                break;
            }
        }

        var target = _production.ShiftTargetCount;
        if (target > 0)
        {
            var progress = Math.Clamp(shiftCount * 100.0 / target, 0, 100);
            ShiftOutputText = $"{shiftCount} / {target} 件";
            ShiftProgress = progress;
            ShiftProgressText = $"{progress:F1}%";
        }
        else
        {
            ShiftOutputText = $"{shiftCount} 件";
            ShiftProgress = 0;
            ShiftProgressText = "未设目标";
        }

        // 标准节拍对比（未配置基准时只显示实际值）
        StandardCycleText = _production.StandardCycleMs > 0
            ? $"标准 {_production.StandardCycleMs / 1000.0:F2} s"
            : "";

        // 实际 UPH = 产量 ÷ 总时长（含停机）；可用率 = 运行 ÷ 总时长
        var totalSeconds = summary.RunSeconds + summary.DowntimeSeconds;
        if (totalSeconds <= 0)
        {
            UphText = "--";
            AvailabilityText = "可用率 --";
        }
        else
        {
            UphText = $"{summary.TotalCount * 3600.0 / totalSeconds:F0}";
            AvailabilityText = $"可用率 {summary.RunSeconds * 100.0 / totalSeconds:F0}%";
        }

        // 停机：当班总时长 + 占时最长的原因
        DowntimeText = FormatMinutes(summary.DowntimeSeconds);

        DowntimeSummary? top = null;
        foreach (var item in _outputService.GetDowntimeSummary(now.Date, now))
        {
            if (top is null || item.TotalSeconds > top.TotalSeconds)
            {
                top = item;
            }
        }

        DowntimeTopText = top is null || top.TotalSeconds == 0
            ? "本班无停机"
            : $"TOP1 {top.DisplayName} {FormatMinutes(top.TotalSeconds)}";
    }

    /// <summary>把秒数格式化成「x 分」或「x 时 x 分」。</summary>
    private static string FormatMinutes(int seconds)
    {
        if (seconds <= 0)
        {
            return "0 min";
        }

        var minutes = seconds / 60;
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60} min";
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

    /// <summary>最后拍照时间：拍照是流程第 6 步（瞬时步骤），进入该步骤即记录时刻。</summary>
    private void UpdateLastCapture(DeviceState state, int stepIndex, DateTime now)
    {
        if (state == DeviceState.Running && stepIndex == CaptureStepIndex)
        {
            _lastCaptureAt = now;
        }

        LastCaptureText = _lastCaptureAt?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "--";
    }



    /// <summary>刷新四轴读数（位置数值、到位状态、行程占比）。</summary>
    private void UpdateAxes(DeviceSnapshot snapshot)
    {
        foreach (var axis in Axes)
        {
            axis.Apply(snapshot);
        }
    }

    /// <summary>刷新视觉结果（纠偏量、MARK 分数、失败说明）。</summary>
    /// <param name="vision">协调器给出的最近一次视觉结果；未拍照过为 <see langword="null"/>。</param>
    private void UpdateVision(VisionCorrection? vision)
    {
        if (vision is null)
        {
            VisionDeltaXText = "--";
            VisionDeltaYText = "--";
            VisionDeltaThetaText = "--";
            VisionScoreText = "--";
            VisionTimeText = "--";
            VisionResultText = "尚未拍照";
            VisionOk = false;
            return;
        }

        VisionDeltaXText = vision.DeltaX.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " mm";
        VisionDeltaYText = vision.DeltaY.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " mm";
        VisionDeltaThetaText = vision.DeltaTheta.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " °";
        VisionScoreText = $"MARK1 {vision.Score1:F2}  MARK2 {vision.Score2:F2}";
        VisionTimeText = vision.ElapsedMs > 0 ? $"{vision.ElapsedMs} ms" : "--";
        VisionOk = vision.Success;
        VisionResultText = vision.Success
            ? "识别正常"
            : "识别失败：" + (vision.ErrorDetail ?? vision.Error.ToString());
    }

    /// <summary>
    /// 把最新一帧转成可显示的位图。版本号未变则直接返回——
    /// 1280×1024 有 130 万像素，每 200ms 转换一次会明显占用 UI 线程。
    /// </summary>
    private void UpdateVisionFrame()
    {
        var frame = _coordinator.LastFrame;
        if (frame is null || frame.Version == _convertedFrameVersion)
        {
            return;
        }

        _convertedFrameVersion = frame.Version;

        try
        {
            var bitmap = BitmapSource.Create(
                frame.Width, frame.Height, 96, 96,
                PixelFormats.Gray8, null, frame.Mono8, frame.Width);
            bitmap.Freeze();
            VisionImage = bitmap;
        }
        catch (ArgumentException ex)
        {
            _log.LogWarning(ex, "视觉帧转换为位图失败（{Width}×{Height}）", frame.Width, frame.Height);
            return;
        }

        var vision = _coordinator.LastVision;
        UpdateMarkOverlay(frame, vision);

        if (vision is not null)
        {
            PushScore(Math.Min(vision.Score1, vision.Score2));
        }
    }

    /// <summary>把图像像素坐标换算成缩略图坐标（Stretch=Uniform：按短边等比缩放后居中）。</summary>
    private void UpdateMarkOverlay(VisionFrameSnapshot frame, VisionCorrection? vision)
    {
        if (vision is null || vision.Mark1X <= 0 || vision.Mark2X <= 0
            || frame.Width <= 0 || frame.Height <= 0)
        {
            ShowMarks = false;
            return;
        }

        var scale = Math.Min(ThumbWidth / frame.Width, ThumbHeight / frame.Height);
        var offsetX = (ThumbWidth - (frame.Width * scale)) / 2;
        var offsetY = (ThumbHeight - (frame.Height * scale)) / 2;

        Mark1CanvasLeft = offsetX + (vision.Mark1X * scale) - MarkRadius;
        Mark1CanvasTop = offsetY + (vision.Mark1Y * scale) - MarkRadius;
        Mark2CanvasLeft = offsetX + (vision.Mark2X * scale) - MarkRadius;
        Mark2CanvasTop = offsetY + (vision.Mark2Y * scale) - MarkRadius;
        ShowMarks = true;
    }

    /// <summary>把本拍分数压入环形缓冲并重算趋势折线（分数缓慢下降是镜头脏 / 光照劣化的早期信号）。</summary>
    private void PushScore(double score)
    {
        _scoreHistory[_scoreHead] = score;
        _scoreHead = (_scoreHead + 1) % ScoreTrendLength;
        if (_scoreCount < ScoreTrendLength)
        {
            _scoreCount++;
        }

        var points = new PointCollection(_scoreCount);
        var start = (_scoreHead - _scoreCount + ScoreTrendLength) % ScoreTrendLength;
        for (var i = 0; i < _scoreCount; i++)
        {
            var value = Math.Clamp(_scoreHistory[(start + i) % ScoreTrendLength], 0, 1);
            var px = i * ThumbWidth / (ScoreTrendLength - 1);
            var py = ScoreTrendHeight - (value * ScoreTrendHeight);
            points.Add(new Point(px, py));
        }

        ScoreTrendPoints = points;
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
