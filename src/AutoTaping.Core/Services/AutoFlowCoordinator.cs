// 职责：自动流程协调器——状态机 + 互锁 + 流程引擎 + 急停 + 运行时故障监视 + 停机记录的统一编排
// 层：Core/Services
// 依赖：IMotionCard/IIoService/ICamera/IDeviceMonitor/IRecipeRepository/ICalibrationRepository/IOutputRepository/
//       IOutputService/IAlarmService/IVisionService/IUserNotifier/IClock（Core/Abstractions）、DeviceStateMachine/
//       InterlockChecker/AutoFlowEngine/FlowContext/15 个 FlowStep、IoPointNames/AlarmLevel/DowntimeReasons（Core/Models）

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Exceptions;
using AutoTaping.Core.Flow;
using AutoTaping.Core.Flow.Steps;
using AutoTaping.Core.Models;
using AutoTaping.Core.StateMachine;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Core.Services;

/// <summary>
/// <see cref="IAutoFlowCoordinator"/> 的默认实现。负责：
/// 注册状态机守卫、执行启动 / 暂停 / 停止 / 复位 / 急停命令、构建 15 个流程步骤并驱动引擎、
/// 后台监视运行时故障（急停 / 安全门 / 气压 / 运控卡 / 相机）。急停处理顺序严格照 05 文档 5.2 节。
/// </summary>
public sealed class AutoFlowCoordinator : IAutoFlowCoordinator
{
    /// <summary>运行时故障监视周期（ms）。</summary>
    private const int WatchIntervalMs = 100;

    /// <summary>回零等待总超时（ms），4 轴并行回零约 1.5s，留足余量。</summary>
    private const int HomeTimeoutMs = 10_000;

    /// <summary>4 个轴的遍历顺序（与 <see cref="AxisId"/> 一致）。</summary>
    private static readonly AxisId[] AllAxes = { AxisId.X, AxisId.Y, AxisId.Z, AxisId.R };

    private readonly IMotionCard _motion;
    private readonly IIoService _io;
    private readonly ICamera _camera;
    private readonly IDeviceMonitor _monitor;
    private readonly IRecipeRepository _recipes;
    private readonly ICalibrationRepository _calibrations;
    private readonly IOutputRepository _outputs;
    private readonly IOutputService _outputService;
    private readonly IAlarmService _alarm;
    private readonly IVisionService _vision;
    private readonly IUserNotifier _notifier;
    private readonly IClock _clock;
    private readonly ILogger<AutoFlowCoordinator> _log;
    private readonly ILoggerFactory _loggerFactory;

    private readonly DeviceStateMachine _stateMachine;
    private readonly InterlockChecker _interlock = new();
    private readonly AutoFlowEngine _engine;

    private readonly CancellationTokenSource _watcherCts = new();
    private readonly object _downtimeSync = new();
    private CancellationTokenSource? _flowCts;
    private Task? _watcherTask;
    private Task? _flowTask;
    private Task? _homingTask;

    private bool _estopReleased;
    private int _lastStepIndex = -1;
    private bool _disposed;

    /// <summary>本次运行的流程上下文（停止后仍保留，便于界面继续显示上一件的视觉结果）。</summary>
    private volatile FlowContext? _currentContext;

    /// <summary>最近一次视觉结果（不可变对象，整体替换引用，界面可无锁读取）。</summary>
    private volatile VisionCorrection? _lastVision;

    /// <summary>最近一帧图像快照（独立副本，整体替换引用）。</summary>
    private volatile VisionFrameSnapshot? _lastFrame;

    /// <summary>已发布到界面的帧版本号，避免每步都复制一次 1.3 MB 像素。</summary>
    private int _publishedFrameVersion;

    /// <summary>当前停机原因代码；<see langword="null"/> 表示未处于停机统计中。</summary>
    private string? _downtimeReason;

    /// <summary>本次停机开始时间（用于恢复时计算时长）。</summary>
    private DateTime _downtimeSince;

    /// <summary>构造协调器并注册守卫、构建步骤、启动监视循环、完成自检。</summary>
    public AutoFlowCoordinator(
        IMotionCard motion,
        IIoService io,
        ICamera camera,
        IDeviceMonitor monitor,
        IRecipeRepository recipes,
        ICalibrationRepository calibrations,
        IOutputRepository outputs,
        IOutputService outputService,
        IAlarmService alarm,
        IVisionService vision,
        IUserNotifier notifier,
        IClock clock,
        ILogger<AutoFlowCoordinator> log,
        ILoggerFactory loggerFactory)
    {
        _motion = motion;
        _io = io;
        _camera = camera;
        _monitor = monitor;
        _recipes = recipes;
        _calibrations = calibrations;
        _outputs = outputs;
        _outputService = outputService;
        _alarm = alarm;
        _vision = vision;
        _notifier = notifier;
        _clock = clock;
        _log = log;
        _loggerFactory = loggerFactory;

        _stateMachine = new DeviceStateMachine(loggerFactory.CreateLogger<DeviceStateMachine>());
        _stateMachine.StateChanged += OnStateMachineChanged;

        RegisterGuards();
        _engine = new AutoFlowEngine(BuildSteps(), loggerFactory.CreateLogger<AutoFlowEngine>());
        _engine.StepCompleted += OnStepCompleted;

        _watcherTask = Task.Run(() => WatchLoopAsync(_watcherCts.Token));

        CompleteSelfCheck();
    }

    /// <inheritdoc />
    public DeviceState State => _stateMachine.State;

    /// <inheritdoc />
    public OperationMode Mode => _stateMachine.Mode;

    /// <inheritdoc />
    public bool IsSingleStep => _engine.SingleStep;

    /// <inheritdoc />
    public int CurrentStepIndex
    {
        get
        {
            var running = _stateMachine.State is DeviceState.Running or DeviceState.Paused;
            return running ? _engine.CurrentIndex + 1 : 0;
        }
    }

    /// <inheritdoc />
    public int TotalSteps => _engine.TotalSteps;

    /// <inheritdoc />
    public string? CurrentStepName
    {
        get
        {
            var running = _stateMachine.State is DeviceState.Running or DeviceState.Paused;
            return running ? _engine.CurrentStep.Name : null;
        }
    }

    /// <inheritdoc />
    public bool IsEmergencyStopReleased => _estopReleased;

    /// <inheritdoc />
    public VisionCorrection? LastVision => _lastVision;

    /// <inheritdoc />
    public VisionFrameSnapshot? LastFrame => _lastFrame;

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public event EventHandler? StepChanged;

    /// <inheritdoc />
    public void CompleteSelfCheck()
    {
        if (_stateMachine.State != DeviceState.Initializing)
        {
            return;
        }

        var snap = _monitor.Current;

        if (!snap.MotionConnected)
        {
            _alarm.Raise("AL105", AlarmLevel.Critical, "运控卡未连接", "运动控制");
            _stateMachine.TryTransition(DeviceState.Alarming);
            return;
        }

        if (SelfCheckReady())
        {
            _stateMachine.TryTransition(DeviceState.Ready);
        }
        else
        {
            _stateMachine.TryTransition(DeviceState.NotReady);
        }
    }

    /// <inheritdoc />
    public void Start()
    {
        if (_stateMachine.State == DeviceState.Paused)
        {
            Resume();
            return;
        }

        if (_stateMachine.State != DeviceState.Ready)
        {
            _notifier.Warn($"当前状态 {GetStateText(_stateMachine.State)} 不允许启动。");
            return;
        }

        var recipe = _recipes.GetActive();
        // 回零状态取硬件真值：快照每 200ms 采样一次，回零刚完成时可能仍是「未回零」，
        // 会让操作工看到「就绪」后点启动却被拒绝一次。
        var errors = _interlock.CheckForStart(_monitor.Current, recipe, _alarm.HasUnackedCritical(),
                                              _motion.IsHomeCompleted);
        if (errors.Count > 0)
        {
            // 05 第 6.1 节：一次列全所有失败原因
            _notifier.Warn("无法启动：" + string.Join("；", errors));
            return;
        }

        _stateMachine.TryTransition(DeviceState.Running);

        // 开始生产即结束当前停机段（人工暂停 / 待料 / 故障排除后恢复）
        EndDowntime();

        // 配置视觉（无生效标定时用 null，流程会在拍照识别步骤给出明确失败）
        var calibration = _calibrations.GetActive();
        _vision.Configure(recipe!.Parameters.Vision, calibration, recipe.Name);

        var ctx = new FlowContext
        {
            Recipe = recipe,
            SequenceNo = _outputs.NextSequenceNo(_clock.Now)
        };

        // 保留上一件的视觉结果：现场最常问的是「上一件偏了多少」
        _currentContext = ctx;

        _log.LogInformation("启动自动流程：配方 {Recipe}", recipe.Name);
        _flowCts = new CancellationTokenSource();
        _flowTask = Task.Run(() => FlowLoopAsync(ctx, _flowCts.Token));
    }

    /// <inheritdoc />
    public void Pause() => PauseCore(DowntimeReasons.ManualPause);

    /// <inheritdoc />
    public void Resume()
    {
        if (!_stateMachine.TryTransition(DeviceState.Running))
        {
            _notifier.Warn("当前状态不允许继续（可能安全门未闭合或存在未确认严重报警）。");
            return;
        }

        _engine.Resume();
        EndDowntime();
        _log.LogInformation("自动流程继续");
        _notifier.Info("已继续。");
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (_stateMachine.State is not (DeviceState.Running or DeviceState.Paused))
        {
            _notifier.Info("设备未运行，无需停止。");
            return;
        }

        _flowCts?.Cancel();
        _io.WriteDo(IoPointNames.ConveyorRun, false);
        _engine.Resume();
        _stateMachine.TryTransition(DeviceState.Ready);
        EndDowntime();
        _log.LogInformation("自动流程已停止");
        _notifier.Info("已停止。");
    }

    /// <inheritdoc />
    public void Reset()
    {
        switch (_stateMachine.State)
        {
            case DeviceState.Alarming:
                ResetFromAlarming();
                break;
            case DeviceState.EStopped:
                ResetFromEStopped();
                break;
            case DeviceState.NotReady:
                StartHoming();
                break;
            default:
                _notifier.Info("当前状态无需复位。");
                break;
        }
    }

    /// <inheritdoc />
    public void EmergencyStop()
    {
        if (_stateMachine.State is DeviceState.EStopped or DeviceState.Exiting)
        {
            return;
        }

        // 05 第 5.2 节：顺序不得调整
        _motion.EmergencyStopAll();                  // 1. 立刻停轴
        _io.ClearAllForcedOutput();                  // 2. 清除强制输出
        _io.WriteDo(IoPointNames.ConveyorRun, false);   // 3. 停传送带（防止载具继续流动）
        _io.WriteDo(IoPointNames.VacuumValve, false);   // 4. 关真空
        _io.WriteDo(IoPointNames.BlowValve, false);      // 5. 关吹气
        _io.WriteDo(IoPointNames.PressCylinder, false);  // 6. 压合气缸抬起
        _io.WriteDo(IoPointNames.TowerLightGreen, false); // 7. 绿灯灭
        _io.WriteDo(IoPointNames.TowerLightRed, true);    // 8. 红灯亮

        _estopReleased = false;
        _flowCts?.Cancel();
        _stateMachine.TryTransition(DeviceState.EStopped);
        BeginDowntime(DowntimeReasons.Fault);
        _alarm.Raise("AL100", AlarmLevel.Critical, "急停被按下", "安全");
        _log.LogWarning("急停触发");
    }

    /// <inheritdoc />
    public void ReleaseEmergencyStop()
    {
        if (_stateMachine.State != DeviceState.EStopped)
        {
            return;
        }

        _estopReleased = true;
        _io.WriteDo(IoPointNames.TowerLightRed, false);
        _log.LogInformation("急停已释放");
        _notifier.Info("急停已释放，请点复位后重新回零。");
    }

    /// <inheritdoc />
    public void SetSingleStep(bool enabled)
    {
        _engine.SingleStep = enabled;
        _log.LogInformation("单步模式 {State}", enabled ? "开启" : "关闭");
    }

    /// <inheritdoc />
    public bool TrySwitchMode(OperationMode mode) => _stateMachine.TrySwitchMode(mode);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _flowCts?.Cancel();
        _watcherCts.Cancel();
        _stateMachine.StateChanged -= OnStateMachineChanged;
        _engine.StepCompleted -= OnStepCompleted;

        _flowCts?.Dispose();
        _watcherCts.Dispose();
    }

    // ==================== 内部实现 ====================

    /// <summary>暂停实现：按停机原因分类记录停机段（05 文档 7.2 节）。</summary>
    /// <param name="reason">停机原因代码（<see cref="DowntimeReasons"/> 之一）。</param>
    private void PauseCore(string reason)
    {
        if (!_stateMachine.TryTransition(DeviceState.Paused))
        {
            _notifier.Warn("当前状态不允许暂停。");
            return;
        }

        _engine.Pause();
        foreach (var axis in AllAxes)
        {
            _motion.Stop(axis, emergency: false);
        }

        // 传送带由放行步骤开、下一拍停，暂停时必须主动关带，否则载具会被带走
        _io.WriteDo(IoPointNames.ConveyorRun, false);

        BeginDowntime(reason);
        _log.LogInformation("自动流程已暂停（{Reason}）", DowntimeReasons.GetDisplayName(reason));
        _notifier.Info("已暂停。");
    }

    /// <summary>开始一段停机统计；已处于停机段时不重复开始（避免重复计数）。</summary>
    /// <param name="reason">停机原因代码。</param>
    private void BeginDowntime(string reason)
    {
        lock (_downtimeSync)
        {
            if (_downtimeReason is not null)
            {
                return;
            }

            _downtimeReason = reason;
            _downtimeSince = _clock.Now;
        }
    }

    /// <summary>结束当前停机段并落库（无停机段时安全无操作）。</summary>
    private void EndDowntime()
    {
        string? reason;
        DateTime since;

        lock (_downtimeSync)
        {
            reason = _downtimeReason;
            since = _downtimeSince;
            _downtimeReason = null;
        }

        if (reason is null)
        {
            return;
        }

        _outputService.RecordDowntime(reason, since, _clock.Now);
    }

    /// <summary>注册状态机守卫（05 第 2 节迁移表）。</summary>
    private void RegisterGuards()
    {
        _stateMachine.RegisterGuard(DeviceState.Initializing, DeviceState.Ready, SelfCheckReady);
        _stateMachine.RegisterGuard(DeviceState.Initializing, DeviceState.NotReady, () => !SelfCheckReady());
        _stateMachine.RegisterGuard(DeviceState.NotReady, DeviceState.Ready, () =>
            _monitor.Current.Di(IoPointNames.SafetyDoorClosed) && _monitor.Current.MotionConnected);
        _stateMachine.RegisterGuard(DeviceState.Ready, DeviceState.Running, () =>
            _stateMachine.Mode == OperationMode.Auto
            && _recipes.GetActive() is not null
            && _monitor.Current.Di(IoPointNames.SafetyDoorClosed)
            && _monitor.Current.CameraConnected
            && _monitor.Current.Di(IoPointNames.AirPressureOk));
        _stateMachine.RegisterGuard(DeviceState.Paused, DeviceState.Running, () =>
            !_alarm.HasUnackedCritical() && _monitor.Current.Di(IoPointNames.SafetyDoorClosed));
        _stateMachine.RegisterGuard(DeviceState.Alarming, DeviceState.Ready, () => !_alarm.HasUnackedCritical());
        _stateMachine.RegisterGuard(DeviceState.EStopped, DeviceState.NotReady, () => _estopReleased);
        _stateMachine.RegisterGuard(DeviceState.Ready, DeviceState.NotReady, () => true);
    }

    /// <summary>启动自检通过条件（4 轴回零 && 相机就绪 && 气压正常 && 无未确认严重报警）。</summary>
    private bool SelfCheckReady()
    {
        var snap = _monitor.Current;
        var allHomed = true;
        foreach (var axis in AllAxes)
        {
            if (!snap.IsHomed(axis))
            {
                allHomed = false;
                break;
            }
        }

        return allHomed
               && snap.CameraConnected
               && snap.Di(IoPointNames.AirPressureOk)
               && !_alarm.HasUnackedCritical();
    }

    /// <summary>
    /// 按 05 第 4.2 节顺序构建 12 个流程步骤（产线 + 点贴：无贴附路径、无下料位移动）。
    /// 相机 Eye-in-Hand（随贴上头一起动），拍照前必须由 <see cref="MoveToPhotoPosStep"/> 把头送到拍照位，
    /// 否则拍到的是头当前位置的正下方（首拍在回零位、后续在贴附位），MARK 会落到视野外。
    /// </summary>
    private IReadOnlyList<FlowStep> BuildSteps()
    {
        return new List<FlowStep>
        {
            new CheckPreconditionsStep(_interlock, _monitor, _alarm),
            new WaitCarrierStep(_io, _recipes, _loggerFactory.CreateLogger<WaitCarrierStep>()),
            new SettleDelayStep(),
            new MoveToPhotoPosStep(_motion),
            new CaptureAndCorrectStep(_camera, _vision),
            new MoveToPickPosStep(_motion),
            new PickTapeStep(_motion, _io),
            new MoveToTapePosStep(_motion),
            new PressAndHoldStep(_motion, _io),
            new ReleaseTapeStep(_motion, _io),
            new RecordOutputStep(_outputs, _clock, _loggerFactory.CreateLogger<RecordOutputStep>()),
            new ConveyOutStep(_io)
        };
    }

    /// <summary>流程线程主体：驱动引擎，捕获步骤失败与整体取消。</summary>
    private async Task FlowLoopAsync(FlowContext ctx, CancellationToken ct)
    {
        try
        {
            await _engine.RunAsync(ctx, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogInformation("自动流程已停止（停止 / 急停）");
        }
        catch (FlowException ex)
        {
            HandleFlowFailure(ex);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "自动流程异常终止");
            _notifier.Error("自动流程异常终止，请查看日志。");
            _stateMachine.TryTransition(DeviceState.Alarming);
        }
    }

    /// <summary>处理流程步骤失败：按 FlowException 的报警码报警并转 Alarming，或按标记只暂停。</summary>
    private void HandleFlowFailure(FlowException ex)
    {
        if (ex.PauseInsteadOfAlarm)
        {
            _log.LogInformation("流程暂停（非故障）：{Message}", ex.Message);
            _notifier.Warn(ex.Message);
            // 走统一暂停路径，保证停轴、关传送带、停机统计一个不漏（如载具等待超时时带子正开着）
            PauseCore(DowntimeReasons.WaitMaterial);
            return;
        }

        if (ex.AlarmCode is not null)
        {
            _alarm.Raise(ex.AlarmCode, ex.AlarmLevel, ex.Message, ex.AlarmSource ?? "");
        }

        _log.LogError(ex, "流程步骤失败：{Step}", ex.StepName);
        _notifier.Error(ex.Message);
        _stateMachine.TryTransition(DeviceState.Alarming);
        BeginDowntime(DowntimeReasons.Fault);
    }

    /// <summary>复位：Alarming → Ready（需无未确认严重报警）。</summary>
    private void ResetFromAlarming()
    {
        if (_alarm.HasUnackedCritical())
        {
            _notifier.Warn("存在未确认的严重报警，请先确认报警。");
            return;
        }

        _alarm.CloseAllActive();
        _stateMachine.TryTransition(DeviceState.Ready);
        EndDowntime();
        _notifier.Info("报警已复位，设备就绪。");
    }

    /// <summary>复位：EStopped → NotReady（需急停已释放，随后 ResetAxis）。</summary>
    private void ResetFromEStopped()
    {
        if (!_estopReleased)
        {
            _notifier.Warn("请先释放急停。");
            return;
        }

        foreach (var axis in AllAxes)
        {
            _motion.ResetAxis(axis);
        }

        _stateMachine.TryTransition(DeviceState.NotReady);
        EndDowntime();
        _notifier.Info("急停已复位，请执行回零。");
    }

    /// <summary>回零：4 轴回零成功后 NotReady → Ready，失败转 Alarming（AL106）。</summary>
    private void StartHoming()
    {
        if (_homingTask is { IsCompleted: false })
        {
            _notifier.Info("正在回零中。");
            return;
        }

        _homingTask = Task.Run(() => HomeAllAsync());
    }

    private async Task HomeAllAsync()
    {
        _log.LogInformation("开始四轴回零");
        foreach (var axis in AllAxes)
        {
            _motion.Home(axis);
        }

        var deadline = Environment.TickCount64 + HomeTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (AllAxesHomed())
            {
                break;
            }

            await Task.Delay(100);
        }

        if (AllAxesHomed())
        {
            _stateMachine.TryTransition(DeviceState.Ready);
            _notifier.Info("四轴回零完成，设备就绪。");
        }
        else
        {
            _alarm.Raise("AL106", AlarmLevel.Warning, "回零失败", "运动控制");
            _stateMachine.TryTransition(DeviceState.Alarming);
            _notifier.Warn("回零失败，请检查原点传感器。");
        }
    }

    private bool AllAxesHomed()
    {
        foreach (var axis in AllAxes)
        {
            if (!_motion.IsHomeCompleted(axis))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>运行时故障监视循环（后台线程，100ms 周期）。</summary>
    private async Task WatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                CheckRuntimeFaults();
                RaiseStepChangedIfNeeded();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "运行时故障监视异常");
            }

            await Task.Delay(WatchIntervalMs, ct);
        }
    }

    /// <summary>检查急停 / 安全门 / 气压 / 运控卡 / 相机故障并按 05 第 5 节策略处理。</summary>
    private void CheckRuntimeFaults()
    {
        var snap = _monitor.Current;
        var state = _stateMachine.State;

        // 急停（任意状态，除 EStopped/Exiting）
        var estopPressed = snap.DiStates.TryGetValue(IoPointNames.EmergencyStopReleased, out var released) && !released;
        if (estopPressed && state is not (DeviceState.EStopped or DeviceState.Exiting))
        {
            EmergencyStop();
            return;
        }

        // 报警条件消失时自动恢复（09 文档第三节：除 AL100/101/103/105 外的报警可自动恢复）
        RecoverClearedAlarms(snap);

        if (state != DeviceState.Running)
        {
            return;
        }

        if (!snap.Di(IoPointNames.SafetyDoorClosed))
        {
            _alarm.Raise("AL401", AlarmLevel.Critical, "安全门被打开", "安全");
            PauseCore(DowntimeReasons.Fault);
            return;
        }

        if (!snap.Di(IoPointNames.AirPressureOk))
        {
            _alarm.Raise("AL402", AlarmLevel.Critical, "气压不足", "气动");
            PauseCore(DowntimeReasons.Fault);
            return;
        }

        if (!snap.MotionConnected)
        {
            _alarm.Raise("AL105", AlarmLevel.Critical, "运控卡通讯中断", "运动控制");
            EmergencyStop();
            return;
        }

        if (!snap.CameraConnected)
        {
            _alarm.Raise("AL201", AlarmLevel.Critical, "相机通讯中断", "视觉");
            PauseCore(DowntimeReasons.Fault);
        }
    }

    /// <summary>
    /// 报警条件消失后自动恢复（写恢复时间，从「当前报警」列表移除）。
    /// 只处理本类自己触发的运行时可恢复报警；<c>AL100/AL101/AL103/AL105</c> 由 <c>AlarmService</c> 内部拒绝自动恢复。
    /// </summary>
    private void RecoverClearedAlarms(DeviceSnapshot snap)
    {
        if (snap.Di(IoPointNames.SafetyDoorClosed))
        {
            _alarm.Recover("AL401");
        }

        if (snap.Di(IoPointNames.AirPressureOk))
        {
            _alarm.Recover("AL402");
        }

        if (snap.CameraConnected)
        {
            _alarm.Recover("AL201");
        }
    }

    /// <summary>当前步骤序号变化时通知 UI（用于主画面高亮当前工位）。</summary>
    private void RaiseStepChangedIfNeeded()
    {
        var index = _stateMachine.State is DeviceState.Running or DeviceState.Paused ? _engine.CurrentIndex : -1;
        if (index == _lastStepIndex)
        {
            return;
        }

        _lastStepIndex = index;
        StepChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnStateMachineChanged(object? sender, StateChangedEventArgs e)
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>步骤完成：单步模式下自动转 Paused（流程线程上触发，状态机线程安全）。</summary>
    private void OnStepCompleted(object? sender, EventArgs e)
    {
        PublishVision();

        if (_engine.SingleStep)
        {
            _stateMachine.TryTransition(DeviceState.Paused);
        }
    }

    /// <summary>把流程上下文里的视觉结果与最新帧发布给界面；只在帧版本变化时复制像素。</summary>
    private void PublishVision()
    {
        var ctx = _currentContext;
        if (ctx is null)
        {
            return;
        }

        var vision = ctx.LastCorrection;
        if (vision is not null)
        {
            _lastVision = vision;
        }

        if (ctx.FrameVersion == _publishedFrameVersion)
        {
            return;
        }

        _publishedFrameVersion = ctx.FrameVersion;
        var bytes = ctx.CopyLastFrame();
        if (bytes is null)
        {
            return;
        }

        _lastFrame = new VisionFrameSnapshot
        {
            Mono8 = bytes,
            Width = ctx.LastFrameWidth,
            Height = ctx.LastFrameHeight,
            Version = ctx.FrameVersion,
            Success = vision?.Success ?? false,
        };
    }

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
