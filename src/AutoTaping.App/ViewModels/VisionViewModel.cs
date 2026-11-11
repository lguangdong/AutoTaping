// 职责：视觉标定页 ViewModel——实时预览 + MARK 识别 + 纠偏显示 + 九点标定向导 + 模板制作 + 标定结果落库
// 层：App/ViewModels
// 依赖：ICamera / IMotionCard / IAuthService / IRecipeRepository / ICalibrationRepository / IOperationLogRepository /
//       IUserNotifier / IUiDispatcher（Core/Abstractions）、VisionOptions / WpfImageConverter（App/Infrastructure）、
//       CalibrationService / CorrectionService / TemplateMatcher / ImageConverter（Vision）、VirtualCamera（Hardware/Camera）
//
// 说明：取图与图像处理都在后台线程，界面属性只在 UI 线程赋值（01-架构设计 第 3 节）；
//       不弹 MessageBox（走 IUserNotifier）；模板匹配器在页面释放时 Dispose。

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using AutoTaping.App.Infrastructure;
using AutoTaping.App.Navigation;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using AutoTaping.Hardware.Camera;
using AutoTaping.Vision;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace AutoTaping.App.ViewModels;

/// <summary>
/// 视觉标定页 ViewModel（08-页面清单 页面 4）。技术员及以上可进。
/// 实时预览叠加 MARK 十字与 ROI 框；九点标定走「平台移 9 点 → 拍照识别 → 拟合仿射 → 残差表」；
/// 模板制作在预览图上框选 ROI 保存；标定结果落库 + 存 9 张原图。
/// </summary>
public sealed partial class VisionViewModel : PageViewModel
{
    /// <summary>标定稳定延时（ms），消除机械振动（04 文档 2.2 节第 3 步）。</summary>
    private const int SettleDelayMs = 200;

    /// <summary>九点标定点数。</summary>
    private const int CalibrationPointCount = 9;

    /// <summary>模板占位符（06 文档 4 节：加载时替换为配方名）。</summary>
    private const string RecipePlaceholder = "{recipe}";

    private readonly ICamera _camera;
    private readonly IMotionCard _motion;
    private readonly IAuthService _auth;
    private readonly IRecipeRepository _recipes;
    private readonly ICalibrationRepository _calibrations;
    private readonly IOperationLogRepository _operationLog;
    private readonly IUserNotifier _notifier;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<VisionViewModel> _log;
    private readonly CalibrationService _calibrationService;
    private readonly CorrectionService _correctionService;
    private readonly VisionOptions _options;

    private readonly TemplateMatcher _matcher1 = new();
    private readonly TemplateMatcher _matcher2 = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _calibrationCts;

    // 当前激活配方的视觉参数（加载配方时填充，用于识别与纠偏）
    private Recipe? _activeRecipe;
    private RoiRect _mark1Roi;
    private RoiRect _mark2Roi;
    private double _matchThreshold = 0.7;
    private double _templateMark1U;
    private double _templateMark1V;
    private double _templateMark2U;
    private double _templateMark2V;
    private double _maxDeltaX = 5.0;
    private double _maxDeltaY = 5.0;
    private double _maxDeltaTheta = 10.0;
    private double _moveSpeed = 100.0;
    private double _safeHeight = 50.0;

    // 标定中间状态（后台线程写入，UI 线程读取）
    private CalibrationResult? _calibrationResult;
    private readonly List<byte[]> _calibrationFrames = new();
    private int _calibrationFrameWidth;
    private int _calibrationFrameHeight;
    private RoiRect? _selectedRoi;

    /// <summary>构造视觉页 ViewModel（由 DI 单例创建，页面进入时才启动预览）。</summary>
    public VisionViewModel(
        ICamera camera,
        IMotionCard motion,
        IAuthService auth,
        IRecipeRepository recipes,
        ICalibrationRepository calibrations,
        IOperationLogRepository operationLog,
        IUserNotifier notifier,
        IUiDispatcher ui,
        ILogger<VisionViewModel> log,
        CalibrationService calibrationService,
        CorrectionService correctionService,
        VisionOptions options)
    {
        _camera = camera;
        _motion = motion;
        _auth = auth;
        _recipes = recipes;
        _calibrations = calibrations;
        _operationLog = operationLog;
        _notifier = notifier;
        _ui = ui;
        _log = log;
        _calibrationService = calibrationService;
        _correctionService = correctionService;
        _options = options;

        CalibrationCenterX = _options.MinLimits[(int)AxisId.X] + 100;
        CalibrationCenterY = _options.MinLimits[(int)AxisId.Y] + 100;
        CalibrationStep = _options.CalibrationStepMm;
        Exposure = _options.DefaultExposureUs;
        Gain = _options.DefaultGainDb;
    }

    /// <inheritdoc />
    public override PageKey Key => PageKey.Vision;

    /// <inheritdoc />
    public override string Title => "视觉标定";

    /// <inheritdoc />
    public override string Icon => "◎";

    /// <inheritdoc />
    public override Permission RequiredPermission => Permission.Calibration;

    // ==================== 预览 ====================

    /// <summary>预览图像（Gray8，已 Freeze，可跨线程）。</summary>
    [ObservableProperty]
    private BitmapSource? _previewImage;

    /// <summary>预览帧宽度（像素），用于 Viewbox 内叠加坐标换算。</summary>
    [ObservableProperty]
    private int _previewWidth = 1280;

    /// <summary>预览帧高度（像素）。</summary>
    [ObservableProperty]
    private int _previewHeight = 1024;

    /// <summary>是否处于连续预览中。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewToggleText))]
    private bool _isPreviewing;

    /// <summary>预览底部状态文字（识别正常 / 失败 / 相机未连接）。</summary>
    [ObservableProperty]
    private string _previewStatus = "未开始预览";

    /// <summary>相机是否已连接。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CameraStatusText))]
    private bool _isCameraConnected;

    /// <summary>曝光（微秒）。</summary>
    [ObservableProperty]
    private double _exposure;

    /// <summary>增益（dB）。</summary>
    [ObservableProperty]
    private double _gain;

    /// <summary>预览切换按钮文字。</summary>
    public string PreviewToggleText => IsPreviewing ? "停止预览" : "连续预览";

    /// <summary>相机连接状态文字。</summary>
    public string CameraStatusText => IsCameraConnected ? "已连接" : "未连接";

    // ==================== 识别结果 ====================

    /// <summary>MARK1 是否识别到（用于叠加十字显隐）。</summary>
    [ObservableProperty]
    private bool _mark1Found;

    /// <summary>MARK2 是否识别到。</summary>
    [ObservableProperty]
    private bool _mark2Found;

    /// <summary>MARK1 中心 X（像素）。</summary>
    [ObservableProperty]
    private double _mark1X;

    /// <summary>MARK1 中心 Y（像素）。</summary>
    [ObservableProperty]
    private double _mark1Y;

    /// <summary>MARK2 中心 X（像素）。</summary>
    [ObservableProperty]
    private double _mark2X;

    /// <summary>MARK2 中心 Y（像素）。</summary>
    [ObservableProperty]
    private double _mark2Y;

    /// <summary>MARK1 识别结果文本。</summary>
    [ObservableProperty]
    private string _mark1Text = "--";

    /// <summary>MARK2 识别结果文本。</summary>
    [ObservableProperty]
    private string _mark2Text = "--";

    /// <summary>匹配分文本（MARK1 / MARK2）。</summary>
    [ObservableProperty]
    private string _matchScoreText = "--";

    /// <summary>角度偏差文本。</summary>
    [ObservableProperty]
    private string _angleText = "--";

    /// <summary>纠偏量文本（ΔX / ΔY / Δθ）。</summary>
    [ObservableProperty]
    private string _deltaText = "--";

    /// <summary>MARK1 搜索 ROI（用于叠加矩形框）。</summary>
    [ObservableProperty]
    private int _mark1RoiX;

    [ObservableProperty]
    private int _mark1RoiY;

    [ObservableProperty]
    private int _mark1RoiW;

    [ObservableProperty]
    private int _mark1RoiH;

    /// <summary>MARK2 搜索 ROI。</summary>
    [ObservableProperty]
    private int _mark2RoiX;

    [ObservableProperty]
    private int _mark2RoiY;

    [ObservableProperty]
    private int _mark2RoiW;

    [ObservableProperty]
    private int _mark2RoiH;

    // ==================== 框选 ROI（模板制作） ====================

    /// <summary>是否有正在框选的 ROI。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveMark1TemplateCommand), nameof(SaveMark2TemplateCommand))]
    private bool _hasSelectedRoi;

    /// <summary>框选 ROI 左上角 X（像素）。</summary>
    [ObservableProperty]
    private double _selectedRoiX;

    /// <summary>框选 ROI 左上角 Y（像素）。</summary>
    [ObservableProperty]
    private double _selectedRoiY;

    /// <summary>框选 ROI 宽度（像素）。</summary>
    [ObservableProperty]
    private double _selectedRoiW;

    /// <summary>框选 ROI 高度（像素）。</summary>
    [ObservableProperty]
    private double _selectedRoiH;

    // ==================== 九点标定 ====================

    /// <summary>标定区域中心 X（mm）。</summary>
    [ObservableProperty]
    private double _calibrationCenterX;

    /// <summary>标定区域中心 Y（mm）。</summary>
    [ObservableProperty]
    private double _calibrationCenterY;

    /// <summary>九宫格步长 L（mm）。</summary>
    [ObservableProperty]
    private double _calibrationStep;

    /// <summary>是否正在标定。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCalibrationCommand), nameof(SaveCalibrationCommand),
                                nameof(SaveMark1TemplateCommand), nameof(SaveMark2TemplateCommand))]
    private bool _isCalibrating;

    /// <summary>标定进度文字（如 6/9）。</summary>
    [ObservableProperty]
    private string _calibrationProgress = "0/9";

    /// <summary>标定结果文字（RMS 与合格判定）。</summary>
    [ObservableProperty]
    private string _calibrationResultText = "尚未标定";

    /// <summary>标定 RMS（mm）。</summary>
    [ObservableProperty]
    private double _rmsError;

    /// <summary>标定结果是否合格（RMS &lt; 0.1mm），用于红色显示不合格。</summary>
    [ObservableProperty]
    private bool _calibrationPassed;

    /// <summary>九点标定残差表。</summary>
    public ObservableCollection<CalibrationPointRow> CalibrationPoints { get; } = new();

    // ==================== 生命周期 ====================

    /// <summary>进入本页：加载激活配方与模板、启动连续预览。</summary>
    public override Task<bool> OnEnteringAsync(CancellationToken ct)
    {
        LoadRecipeAndTemplates();
        StartPreview();
        return Task.FromResult(true);
    }

    /// <summary>离开本页：停止预览与标定。</summary>
    public override Task OnLeaveAsync(CancellationToken ct)
    {
        StopPreview();
        StopCalibration();
        _log.LogInformation("离开视觉页：已停止预览与标定");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override void DisposeCore()
    {
        StopPreview();
        StopCalibration();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _matcher1.Dispose();
        _matcher2.Dispose();
    }

    // ==================== 预览命令 ====================

    /// <summary>开始 / 停止连续预览。</summary>
    [RelayCommand]
    private void TogglePreview()
    {
        if (IsPreviewing)
        {
            StopPreview();
        }
        else
        {
            StartPreview();
        }
    }

    /// <summary>单次拍照并识别一帧。</summary>
    [RelayCommand]
    private async Task CaptureOnceAsync()
    {
        try
        {
            EnsureCameraReady();
            var frame = await _camera.TriggerAndGrabAsync(_options.CaptureTimeoutMs, _lifetime.Token);
            if (!frame.Success || frame.Mono8Data is null)
            {
                _notifier.Warn(frame.ErrorMessage ?? "拍照失败");
                return;
            }

            await ProcessFrameAsync(frame);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "单次拍照失败");
            _notifier.Error("拍照失败，请检查相机状态。");
        }
    }

    /// <summary>应用曝光与增益。</summary>
    [RelayCommand]
    private void ApplyCameraSettings()
    {
        try
        {
            EnsureCameraReady();
            _camera.SetExposure(Exposure);
            _camera.SetGain(Gain);
            _log.LogInformation("应用相机参数：曝光 {Exposure:F0}μs 增益 {Gain:F1}dB", Exposure, Gain);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "应用相机参数失败");
            _notifier.Warn("相机参数应用失败，请检查相机连接。");
        }
    }

    /// <summary>演示用：注入固定偏移 (3, -3)px，用于验证纠偏计算（验收标准 4）。</summary>
    [RelayCommand]
    private void InjectOffsetDemo()
    {
        if (_camera is VirtualCamera virtualCamera)
        {
            virtualCamera.InjectOffset(3, -3);
            _notifier.Info("已注入偏移 (+3, -3)px，预览将显示对应纠偏量。");
        }
        else
        {
            _notifier.Info("真实相机模式下无需注入偏移。");
        }
    }

    // ==================== 标定命令 ====================

    /// <summary>开始九点标定：二次确认后走 9 点拍照识别流程。</summary>
    [RelayCommand(CanExecute = nameof(CanStartCalibration))]
    private async Task StartCalibrationAsync()
    {
        if (!ValidateCalibrationInput(out var message))
        {
            _notifier.Warn(message);
            return;
        }

        if (!_notifier.Confirm("开始九点标定", "标定将移动平台到 9 个位置并拍照，过程中请勿靠近设备。确认开始？"))
        {
            return;
        }

        _log.LogInformation("开始九点标定：中心 ({CenterX:F2}, {CenterY:F2})mm 步长 {Step:F2}mm",
                            CalibrationCenterX, CalibrationCenterY, CalibrationStep);

        StopPreview();

        IsCalibrating = true;
        _calibrationResult = null;
        _calibrationFrames.Clear();
        _calibrationCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);

        try
        {
            await RunCalibrationAsync(_calibrationCts.Token);
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("九点标定被用户中断");
            _notifier.Info("标定已中断，未保存。");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "九点标定异常终止");
            _notifier.Error($"标定失败：{ex.Message}");
        }
        finally
        {
            IsCalibrating = false;
            _calibrationCts.Dispose();
            _calibrationCts = null;
        }
    }

    /// <summary>停止九点标定。</summary>
    [RelayCommand]
    private void StopCalibration()
    {
        var cts = _calibrationCts;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        _log.LogInformation("请求停止九点标定");
    }

    /// <summary>保存标定结果：落库 + 存 9 张原图 + 置 active + 写操作日志。</summary>
    [RelayCommand(CanExecute = nameof(CanSaveCalibration))]
    private void SaveCalibration()
    {
        var result = _calibrationResult;
        if (result is null || !result.IsValid)
        {
            _notifier.Warn("标定结果不合格，不能保存。");
            return;
        }

        try
        {
            var folder = SaveCalibrationImages();
            var user = _auth.CurrentUser?.UserName ?? "";

            _calibrations.Insert(new Calibration
            {
                AffineMatrix = result.AffineMatrix,
                RmsError = result.RmsError,
                PointCount = result.PointCount,
                ImageFolder = folder,
                CreatedBy = user
            });

            _operationLog.Insert(user, "标定", "九点标定",
                oldValue: null, newValue: $"RMS={result.RmsError:F4}mm");

            _log.LogInformation("标定结果已保存：RMS={Rms:F4}mm 图片目录={Folder}", result.RmsError, folder);
            _notifier.Info($"标定已保存（RMS {result.RmsError:F4} mm）并设为当前生效标定。");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "保存标定结果失败");
            _notifier.Error("保存标定失败，请检查磁盘空间与数据目录权限。");
        }
    }

    // ==================== 模板制作 ====================

    /// <summary>把框选的 ROI 保存为 MARK1 模板并更新配方。</summary>
    [RelayCommand(CanExecute = nameof(CanSaveTemplate))]
    private Task SaveMark1TemplateAsync() => SaveMarkTemplateAsync(markIndex: 1);

    /// <summary>把框选的 ROI 保存为 MARK2 模板并更新配方。</summary>
    [RelayCommand(CanExecute = nameof(CanSaveTemplate))]
    private Task SaveMark2TemplateAsync() => SaveMarkTemplateAsync(markIndex: 2);

    // ==================== View 转发：鼠标框选 ROI ====================

    /// <summary>由 View 的鼠标拖拽转发：记录框选 ROI（像素坐标）。</summary>
    public void SetRoiSelection(double x, double y, double width, double height)
    {
        if (width < 4 || height < 4)
        {
            HasSelectedRoi = false;
            _selectedRoi = null;
            return;
        }

        _selectedRoi = new RoiRect((int)x, (int)y, (int)width, (int)height);
        SelectedRoiX = x;
        SelectedRoiY = y;
        SelectedRoiW = width;
        SelectedRoiH = height;
        HasSelectedRoi = true;
    }

    // ==================== 私有：预览 ====================

    private bool CanStartCalibration() => !IsCalibrating;

    private bool CanSaveCalibration() => _calibrationResult is not null && _calibrationResult.IsValid && !IsCalibrating;

    private bool CanSaveTemplate() => HasSelectedRoi && !IsCalibrating;

    /// <summary>确保相机已初始化并处于采集状态。</summary>
    private void EnsureCameraReady()
    {
        if (!_camera.IsConnected)
        {
            _camera.Initialize();
        }

        if (!_camera.IsGrabbing)
        {
            _camera.StartGrabbing();
        }
    }

    /// <summary>启动连续预览循环（重复调用先停旧循环）。</summary>
    private void StartPreview()
    {
        if (IsPreviewing)
        {
            return;
        }

        EnsureCameraReady();
        IsCameraConnected = _camera.IsConnected;

        if (!IsCameraConnected)
        {
            PreviewStatus = "相机未连接";
            _notifier.Warn("相机未连接，无法预览。");
            return;
        }

        StopPreviewLoop();

        _previewCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _previewCts.Token;
        IsPreviewing = true;
        _ = Task.Run(() => PreviewLoopAsync(token), token);
    }

    private void StopPreview()
    {
        IsPreviewing = false;
        StopPreviewLoop();
    }

    private void StopPreviewLoop()
    {
        var cts = _previewCts;
        if (cts is null)
        {
            return;
        }

        _previewCts = null;
        cts.Cancel();
        cts.Dispose();
    }

    /// <summary>预览循环：串行软触发取图 → 识别 → 转图 → 切 UI 线程显示。</summary>
    private async Task PreviewLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                CaptureResult frame;
                try
                {
                    frame = await _camera.TriggerAndGrabAsync(_options.CaptureTimeoutMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "预览取图失败");
                    await Task.Delay(500, ct);
                    continue;
                }

                if (!frame.Success || frame.Mono8Data is null)
                {
                    await Task.Delay(200, ct);
                    continue;
                }

                await ProcessFrameAsync(frame, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 切页/退出取消预览是正常路径
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "预览循环异常终止");
        }
        finally
        {
            if (_previewCts is not null && !_previewCts.IsCancellationRequested)
            {
                // 循环意外退出时同步状态
                await _ui.InvokeAsync(() => IsPreviewing = false);
            }
        }
    }

    /// <summary>处理一帧：ToMat → 识别 → 转 BitmapSource → UI 线程显示。</summary>
    private async Task ProcessFrameAsync(CaptureResult frame, CancellationToken ct = default)
    {
        using var mat = ImageConverter.ToMat(frame.Mono8Data!, frame.Width, frame.Height);
        var recognition = Recognize(mat);
        var bitmap = WpfImageConverter.ToBitmapSource(mat);

        await _ui.InvokeAsync(() => ApplyFrame(bitmap, recognition, frame.Width, frame.Height));
    }

    /// <summary>识别两个 MARK 并计算纠偏（后台线程执行）。</summary>
    private (TemplateMatchResult? Mark1, TemplateMatchResult? Mark2, CorrectionResult? Correction) Recognize(Mat mat)
    {
        var m1 = _matcher1.Match(mat, ToRect(_mark1Roi), _matchThreshold);
        var m2 = _matcher2.Match(mat, ToRect(_mark2Roi), _matchThreshold);

        CorrectionResult? corr = null;
        if (m1 is not null && m2 is not null && _calibrationResult is not null)
        {
            var template1 = new Point2f((float)_templateMark1U, (float)_templateMark1V);
            var template2 = new Point2f((float)_templateMark2U, (float)_templateMark2V);
            corr = _correctionService.Compute(
                m1.Value.Center, m2.Value.Center, template1, template2,
                _calibrationResult, _maxDeltaX, _maxDeltaY, _maxDeltaTheta);
        }

        return (m1, m2, corr);
    }

    /// <summary>把识别结果写入界面（必须在 UI 线程调用）。</summary>
    private void ApplyFrame(
        BitmapSource bitmap,
        (TemplateMatchResult? Mark1, TemplateMatchResult? Mark2, CorrectionResult? Correction) recognition,
        int width, int height)
    {
        PreviewImage = bitmap;
        PreviewWidth = width;
        PreviewHeight = height;

        var (m1, m2, corr) = recognition;

        Mark1Found = m1 is not null;
        Mark2Found = m2 is not null;

        if (m1 is not null)
        {
            Mark1X = m1.Value.Center.X;
            Mark1Y = m1.Value.Center.Y;
        }

        if (m2 is not null)
        {
            Mark2X = m2.Value.Center.X;
            Mark2Y = m2.Value.Center.Y;
        }

        Mark1Text = m1 is null ? "MARK1 未识别" : $"MARK1 ({m1.Value.Center.X:F1}, {m1.Value.Center.Y:F1})";
        Mark2Text = m2 is null ? "MARK2 未识别" : $"MARK2 ({m2.Value.Center.X:F1}, {m2.Value.Center.Y:F1})";
        MatchScoreText = m1 is null || m2 is null
            ? "--"
            : $"{m1.Value.Score:F2} / {m2.Value.Score:F2}";

        if (corr is not null && corr.IsValid)
        {
            AngleText = $"{corr.DeltaTheta:F2}°";
            DeltaText = $"ΔX {corr.DeltaX:F3} mm  ΔY {corr.DeltaY:F3} mm  Δθ {corr.DeltaTheta:F2}°";
            PreviewStatus = "识别正常";
        }
        else if (corr is not null && !corr.IsValid)
        {
            AngleText = "--";
            DeltaText = corr.Message ?? "纠偏量超限";
            PreviewStatus = "纠偏量超限";
        }
        else
        {
            AngleText = "--";
            DeltaText = "--";
            PreviewStatus = m1 is null || m2 is null ? "MARK 识别失败" : "未标定，无纠偏";
        }
    }

    // ==================== 私有：配方与模板 ====================

    /// <summary>加载激活配方与视觉参数，加载模板，读取当前生效标定。</summary>
    private void LoadRecipeAndTemplates()
    {
        _activeRecipe = _recipes.GetActive();
        if (_activeRecipe is null)
        {
            _log.LogWarning("无激活配方，视觉页使用默认视觉参数与标定中心");
            LoadDefaultVisionParameters();
            return;
        }

        var vision = _activeRecipe.Parameters.Vision;
        var position = _activeRecipe.Parameters.Position;
        var process = _activeRecipe.Parameters.Process;

        _mark1Roi = vision.Mark1Roi;
        _mark2Roi = vision.Mark2Roi;
        _matchThreshold = vision.MatchThreshold;
        _templateMark1U = vision.TemplateMark1U;
        _templateMark1V = vision.TemplateMark1V;
        _templateMark2U = vision.TemplateMark2U;
        _templateMark2V = vision.TemplateMark2V;
        _maxDeltaX = vision.MaxDeltaXMm;
        _maxDeltaY = vision.MaxDeltaYMm;
        _maxDeltaTheta = vision.MaxDeltaThetaDeg;
        _moveSpeed = process.MoveSpeedMmPerSec;
        _safeHeight = process.SafeHeightMm;

        CalibrationCenterX = position.PhotoPosX;
        CalibrationCenterY = position.PhotoPosY;

        ApplyRoiToUi();

        LoadTemplate(vision.Template1, _matcher1);
        LoadTemplate(vision.Template2, _matcher2);

        LoadActiveCalibration();
    }

    /// <summary>无激活配方时使用默认视觉参数（ROI 与阈值取 VisionParameters 默认值）。</summary>
    private void LoadDefaultVisionParameters()
    {
        var vision = new VisionParameters();
        _mark1Roi = vision.Mark1Roi;
        _mark2Roi = vision.Mark2Roi;
        _matchThreshold = vision.MatchThreshold;
        _templateMark1U = vision.TemplateMark1U;
        _templateMark1V = vision.TemplateMark1V;
        _templateMark2U = vision.TemplateMark2U;
        _templateMark2V = vision.TemplateMark2V;
        _maxDeltaX = vision.MaxDeltaXMm;
        _maxDeltaY = vision.MaxDeltaYMm;
        _maxDeltaTheta = vision.MaxDeltaThetaDeg;
        ApplyRoiToUi();
    }

    /// <summary>把 MARK ROI 写入叠加属性（UI 线程）。</summary>
    private void ApplyRoiToUi()
    {
        Mark1RoiX = _mark1Roi.X;
        Mark1RoiY = _mark1Roi.Y;
        Mark1RoiW = _mark1Roi.W;
        Mark1RoiH = _mark1Roi.H;

        Mark2RoiX = _mark2Roi.X;
        Mark2RoiY = _mark2Roi.Y;
        Mark2RoiW = _mark2Roi.W;
        Mark2RoiH = _mark2Roi.H;
    }

    /// <summary>加载模板文件；模板不存在时记录警告但不阻断（进入页仍可预览，只是无法识别）。</summary>
    private void LoadTemplate(string relativePath, TemplateMatcher matcher)
    {
        var fullPath = ResolveTemplatePath(relativePath);
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
        {
            _log.LogWarning("模板文件不存在，MARK 将无法识别：{Path}", fullPath);
            return;
        }

        matcher.LoadTemplate(fullPath);
        _log.LogInformation("已加载模板：{Path}", fullPath);
    }

    /// <summary>把模板相对路径转绝对路径，并替换 {recipe} 占位符。</summary>
    private string ResolveTemplatePath(string relativePath)
    {
        var resolved = relativePath;
        if (resolved.Contains(RecipePlaceholder, StringComparison.Ordinal))
        {
            resolved = resolved.Replace(RecipePlaceholder, _activeRecipe?.Name ?? "default", StringComparison.Ordinal);
        }

        return Path.Combine(AppContext.BaseDirectory, resolved);
    }

    /// <summary>读取当前生效标定并转为 Vision 层的 CalibrationResult。</summary>
    private void LoadActiveCalibration()
    {
        var active = _calibrations.GetActive();
        if (active is null)
        {
            _calibrationResult = null;
            _log.LogInformation("当前无生效标定结果，纠偏功能不可用");
            return;
        }

        _calibrationResult = new CalibrationResult
        {
            AffineMatrix = active.AffineMatrix,
            RmsError = active.RmsError,
            CreatedAt = active.CreatedAt,
            PointCount = active.PointCount
        };

        _log.LogInformation("已加载当前生效标定：RMS={Rms:F4}mm", active.RmsError);
    }

    // ==================== 私有：九点标定 ====================

    /// <summary>校验标定输入（中心与步长、9 个点都在软限位内）。</summary>
    private bool ValidateCalibrationInput(out string message)
    {
        message = "";

        var step = CalibrationStep;
        if (step < 10)
        {
            message = "标定步长过小（< 10mm），误差会被放大，请加大步长（04 文档 2.4 节）。";
            return false;
        }

        var points = GenerateNinePoints(CalibrationCenterX, CalibrationCenterY, step);
        foreach (var (x, y) in points)
        {
            if (x < _options.MinLimits[(int)AxisId.X] || x > _options.MaxLimits[(int)AxisId.X] ||
                y < _options.MinLimits[(int)AxisId.Y] || y > _options.MaxLimits[(int)AxisId.Y])
            {
                message = $"标定点 ({x:F1}, {y:F1}) 超出平台软限位，请调整标定中心或步长。";
                return false;
            }
        }

        return true;
    }

    /// <summary>生成九宫格点位（顺序自左上到右下，见 04 文档 2.2 节）。</summary>
    private static IReadOnlyList<(double X, double Y)> GenerateNinePoints(double centerX, double centerY, double step)
    {
        var points = new List<(double X, double Y)>(CalibrationPointCount);
        for (var row = 1; row >= -1; row--)
        {
            for (var col = -1; col <= 1; col++)
            {
                points.Add((centerX + col * step, centerY + row * step));
            }
        }

        return points;
    }

    /// <summary>九点标定主流程（后台执行，进度经 UI 线程更新）。</summary>
    private async Task RunCalibrationAsync(CancellationToken ct)
    {
        var points = GenerateNinePoints(CalibrationCenterX, CalibrationCenterY, CalibrationStep);
        var pixelPoints = new List<Point2f>(CalibrationPointCount);

        // 安全：Z 抬到安全高度
        await _motion.MoveAndWaitAsync(AxisId.Z, _safeHeight, _moveSpeed, _options.CaptureTimeoutMs, ct);

        for (var i = 0; i < points.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var (x, y) = points[i];

            var movedX = await _motion.MoveAndWaitAsync(AxisId.X, x, _moveSpeed, _options.CaptureTimeoutMs, ct);
            var movedY = await _motion.MoveAndWaitAsync(AxisId.Y, y, _moveSpeed, _options.CaptureTimeoutMs, ct);
            if (!movedX || !movedY)
            {
                throw new InvalidOperationException($"第 {i + 1} 点平台移动失败，标定中止");
            }

            if (_camera is VirtualCamera virtualCamera)
            {
                virtualCamera.SetPlatformPosition(x, y);
            }

            await Task.Delay(SettleDelayMs, ct);

            var frame = await _camera.TriggerAndGrabAsync(_options.CaptureTimeoutMs, ct);
            if (!frame.Success || frame.Mono8Data is null)
            {
                throw new InvalidOperationException($"第 {i + 1} 点拍照失败，标定中止");
            }

            var mark = await Task.Run(() => RecognizeCalibrationMark(frame), ct);
            if (mark is null)
            {
                throw new InvalidOperationException($"第 {i + 1} 点识别失败，请检查光照与模板（08 页面 4）");
            }

            pixelPoints.Add(mark.Value);
            _calibrationFrames.Add(frame.Mono8Data);
            _calibrationFrameWidth = frame.Width;
            _calibrationFrameHeight = frame.Height;

            await _ui.InvokeAsync(() => CalibrationProgress = $"{i + 1}/{CalibrationPointCount}");
        }

        var result = _calibrationService.Calibrate(
            pixelPoints.ToArray(),
            points.Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray());

        _calibrationResult = result;

        await _ui.InvokeAsync(() => ApplyCalibrationResult(result, pixelPoints, points));
    }

    /// <summary>标定时识别 MARK1（左半图搜索，后台线程执行）。</summary>
    private Point2f? RecognizeCalibrationMark(CaptureResult frame)
    {
        using var mat = ImageConverter.ToMat(frame.Mono8Data!, frame.Width, frame.Height);
        var roi = new Rect(0, 0, frame.Width / 2, frame.Height);
        var result = _matcher1.Match(mat, roi, _matchThreshold);
        return result?.Center;
    }

    /// <summary>把标定结果写入界面（必须在 UI 线程调用）。</summary>
    private void ApplyCalibrationResult(
        CalibrationResult result,
        IReadOnlyList<Point2f> pixelPoints,
        IReadOnlyList<(double X, double Y)> platformPoints)
    {
        CalibrationPoints.Clear();
        for (var i = 0; i < pixelPoints.Count; i++)
        {
            var (px, py) = result.PixelToPlatform(pixelPoints[i].X, pixelPoints[i].Y);
            var residual = Math.Sqrt(
                (px - platformPoints[i].X) * (px - platformPoints[i].X) +
                (py - platformPoints[i].Y) * (py - platformPoints[i].Y));

            CalibrationPoints.Add(new CalibrationPointRow(
                i + 1, platformPoints[i].X, platformPoints[i].Y,
                pixelPoints[i].X, pixelPoints[i].Y, residual));
        }

        RmsError = result.RmsError;
        CalibrationPassed = result.IsValid;
        CalibrationResultText = result.IsValid
            ? $"RMS = {result.RmsError:F4} mm  ✅ 合格（< {CalibrationResult.RmsPassThresholdMm:F2} mm）"
            : $"RMS = {result.RmsError:F4} mm  ❌ 不合格（≥ {CalibrationResult.RmsPassThresholdMm:F2} mm），请检查机械/镜头/特征识别后重标";

        SaveCalibrationCommand.NotifyCanExecuteChanged();
        StartPreview();
    }

    /// <summary>保存 9 张标定原图，返回图片目录（相对程序根目录）。</summary>
    private string SaveCalibrationImages()
    {
        var folder = Path.Combine(
            "images", "calibration",
            DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));

        var fullFolder = Path.Combine(AppContext.BaseDirectory, folder);
        Directory.CreateDirectory(fullFolder);

        for (var i = 0; i < _calibrationFrames.Count; i++)
        {
            using var mat = ImageConverter.ToMat(
                _calibrationFrames[i], _calibrationFrameWidth, _calibrationFrameHeight);
            Cv2.ImWrite(Path.Combine(fullFolder, $"point{i + 1}.png"), mat);
        }

        _log.LogInformation("已保存 {Count} 张标定原图到 {Folder}", _calibrationFrames.Count, folder);
        return folder;
    }

    // ==================== 私有：模板制作 ====================

    private async Task SaveMarkTemplateAsync(int markIndex)
    {
        if (_selectedRoi is not { } roi)
        {
            _notifier.Warn("请先在预览图上框选 MARK 区域。");
            return;
        }

        try
        {
            EnsureCameraReady();
            var frame = await _camera.TriggerAndGrabAsync(_options.CaptureTimeoutMs, _lifetime.Token);
            if (!frame.Success || frame.Mono8Data is null)
            {
                _notifier.Warn("拍照失败，无法制作模板。");
                return;
            }

            using var mat = ImageConverter.ToMat(frame.Mono8Data!, frame.Width, frame.Height);
            var rect = ToRect(roi);

            var relativePath = markIndex == 1
                ? "templates/{recipe}/mark1.png"
                : "templates/{recipe}/mark2.png";
            var fullPath = ResolveTemplatePath(relativePath);

            ImageConverter.SaveRoiPng(mat, rect, fullPath);

            // 用刚保存的模板匹配找精确中心，作为模板基准坐标（与后续识别口径一致）
            var matcher = markIndex == 1 ? _matcher1 : _matcher2;
            matcher.LoadTemplate(fullPath);
            var match = matcher.Match(mat, new Rect(0, 0, frame.Width, frame.Height), 0.5);
            if (match is null)
            {
                _notifier.Warn("模板已保存，但在当前图中未找到对应 MARK，请重新框选更准确的区域。");
                return;
            }

            UpdateRecipeTemplate(markIndex, rect, relativePath, match.Value.Center);

            _notifier.Info($"MARK{markIndex} 模板已保存：{fullPath}");
            _log.LogInformation("MARK{Index} 模板已制作：{Path}，基准像素 ({U:F1}, {V:F1})",
                               markIndex, fullPath, match.Value.Center.X, match.Value.Center.Y);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "制作 MARK{Index} 模板失败", markIndex);
            _notifier.Error("模板制作失败，请检查磁盘空间与模板目录权限。");
        }
    }

    /// <summary>更新激活配方的视觉参数（ROI / 模板路径 / 模板基准坐标）并落库。</summary>
    private void UpdateRecipeTemplate(int markIndex, Rect roi, string relativePath, Point2f center)
    {
        var recipe = _activeRecipe;
        if (recipe is null)
        {
            _log.LogWarning("无激活配方，模板已保存但配方视觉参数未更新");
            return;
        }

        var vision = recipe.Parameters.Vision;
        var templatePath = relativePath.Replace(RecipePlaceholder, recipe.Name, StringComparison.Ordinal);

        if (markIndex == 1)
        {
            vision.Mark1Roi = new RoiRect(roi.X, roi.Y, roi.Width, roi.Height);
            vision.Template1 = templatePath;
            vision.TemplateMark1U = center.X;
            vision.TemplateMark1V = center.Y;
            _mark1Roi = vision.Mark1Roi;
            _templateMark1U = center.X;
            _templateMark1V = center.Y;
        }
        else
        {
            vision.Mark2Roi = new RoiRect(roi.X, roi.Y, roi.Width, roi.Height);
            vision.Template2 = templatePath;
            vision.TemplateMark2U = center.X;
            vision.TemplateMark2V = center.Y;
            _mark2Roi = vision.Mark2Roi;
            _templateMark2U = center.X;
            _templateMark2V = center.Y;
        }

        recipe.UpdatedBy = _auth.CurrentUser?.UserName ?? "";
        _recipes.Update(recipe);
        ApplyRoiToUi();
    }

    // ==================== 工具 ====================

    private static Rect ToRect(RoiRect roi) => new(roi.X, roi.Y, roi.W, roi.H);

    /// <summary>九点标定残差表的一行（展示模型，只读）。</summary>
    /// <param name="Index">序号。</param>
    /// <param name="PlatformX">平台 X（mm）。</param>
    /// <param name="PlatformY">平台 Y（mm）。</param>
    /// <param name="PixelU">像素 U。</param>
    /// <param name="PixelV">像素 V。</param>
    /// <param name="Residual">该点残差（mm）。</param>
    public sealed record CalibrationPointRow(
        int Index, double PlatformX, double PlatformY, double PixelU, double PixelV, double Residual);
}
