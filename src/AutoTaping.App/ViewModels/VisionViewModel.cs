// 职责：视觉标定页 ViewModel——单次拍照 + 手动识别 + 纠偏显示 + 九点标定向导 + 模板制作 + 标定结果落库
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
/// 单次拍照显示一帧（叠加 MARK 十字与 ROI 框），识别由操作员手动触发；
/// 九点标定走「平台移 9 点 → 拍照识别 → 拟合仿射 → 残差表」；
/// 模板制作在已拍摄的画面上框选 ROI 保存；标定结果落库 + 存 9 张原图。
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
    private CancellationTokenSource? _calibrationCts;

    // 当前激活配方的视觉参数（加载配方时填充，用于识别与纠偏）
    private Recipe? _activeRecipe;
    private RoiRect _searchRoi;
    private double _markDistanceTolerance = 20.0;
    private double _mark1Radius = 15.0;
    private double _mark2Radius = 9.0;
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

    /// <summary>最近一次拍摄的帧：识别与模板制作都基于它，避免重复取图导致画面与操作对象不一致。</summary>
    private CaptureResult? _lastFrame;

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

    /// <summary>底部状态文字（已拍照待识别 / 识别正常 / 失败 / 相机未连接）。</summary>
    [ObservableProperty]
    private string _previewStatus = "点击「拍照」获取图像";

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

    /// <summary>两个 MARK 共用的搜索区域（用于叠加矩形框）。</summary>
    [ObservableProperty]
    private int _searchRoiX;

    [ObservableProperty]
    private int _searchRoiY;

    [ObservableProperty]
    private int _searchRoiW;

    [ObservableProperty]
    private int _searchRoiH;

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

    /// <summary>进入本页：加载激活配方与模板（取图由操作员点「拍照」触发，不再自动连续预览）。</summary>
    public override Task<bool> OnEnteringAsync(CancellationToken ct)
    {
        LoadRecipeAndTemplates();
        IsCameraConnected = _camera.IsConnected;
        return Task.FromResult(true);
    }

    /// <summary>离开本页：停止标定。</summary>
    public override Task OnLeaveAsync(CancellationToken ct)
    {
        StopCalibration();
        _log.LogInformation("离开视觉页：已停止标定");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override void DisposeCore()
    {
        StopCalibration();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _matcher1.Dispose();
        _matcher2.Dispose();
    }

    // ==================== 拍照 / 识别命令 ====================

    /// <summary>拍一张照片并显示；识别由操作员随后点「识别」手动触发。</summary>
    [RelayCommand]
    private async Task TakePhotoAsync()
    {
        try
        {
            EnsureCameraReady();
            IsCameraConnected = _camera.IsConnected;

            var frame = await _camera.TriggerAndGrabAsync(_options.CaptureTimeoutMs, _lifetime.Token);
            if (!frame.Success || frame.Mono8Data is null)
            {
                PreviewStatus = "拍照失败";
                _notifier.Warn(frame.ErrorMessage ?? "拍照失败");
                return;
            }

            _lastFrame = frame;
            RecognizeCommand.NotifyCanExecuteChanged();

            using var mat = ImageConverter.ToMat(frame.Mono8Data, frame.Width, frame.Height);
            await ShowFrameAsync(mat, frame.Width, frame.Height);

            _log.LogInformation("已拍照：{Width}x{Height}", frame.Width, frame.Height);
        }
        catch (OperationCanceledException)
        {
            // 切页/退出取消取图属正常路径
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "拍照失败");
            _notifier.Error("拍照失败，请检查相机状态。");
        }
    }

    /// <summary>对最近一张照片执行识别（图像处理在后台线程）。</summary>
    [RelayCommand(CanExecute = nameof(CanRecognize))]
    private async Task RecognizeAsync()
    {
        var frame = _lastFrame;
        if (frame?.Mono8Data is null)
        {
            return;
        }

        try
        {
            var recognition = await Task.Run(
                () =>
                {
                    using var mat = ImageConverter.ToMat(frame.Mono8Data, frame.Width, frame.Height);
                    return Recognize(mat);
                },
                _lifetime.Token);

            await _ui.InvokeAsync(() => ApplyRecognition(recognition));
        }
        catch (OperationCanceledException)
        {
            // 切页/退出取消识别属正常路径
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "识别失败");
            _notifier.Error("识别失败，请检查模板与相机状态。");
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

            // 退出平台位置模拟：之后取图恢复「随机样本图」模式
            if (_camera is VirtualCamera virtualCamera)
            {
                virtualCamera.ClearPlatformPosition();
            }
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
    private void SaveMark1Template() => SaveMarkTemplate(markIndex: 1);

    /// <summary>把框选的 ROI 保存为 MARK2 模板并更新配方。</summary>
    [RelayCommand(CanExecute = nameof(CanSaveTemplate))]
    private void SaveMark2Template() => SaveMarkTemplate(markIndex: 2);

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

    /// <summary>
    /// 生成"以指定点为中心、尺寸与给定窗口相同"的裁剪窗口（自动夹到图像内）。
    /// 模板中心约定为 (边长 − 1)/2，故左上角 = 圆心 − (边长 − 1)/2。
    /// </summary>
    private static Rect CenterWindowOn(Rect window, Point2f center, int imageWidth, int imageHeight)
    {
        var left = (int)Math.Round(center.X - ((window.Width - 1) / 2.0));
        var top = (int)Math.Round(center.Y - ((window.Height - 1) / 2.0));

        left = Math.Clamp(left, 0, Math.Max(0, imageWidth - window.Width));
        top = Math.Clamp(top, 0, Math.Max(0, imageHeight - window.Height));

        return new Rect(left, top, window.Width, window.Height);
    }

    /// <summary>清除框选区域：模板保存成功或重新拍照后调用，避免旧框继续留在画面上。</summary>
    private void ClearRoiSelection()
    {
        _selectedRoi = null;
        HasSelectedRoi = false;
        SelectedRoiX = 0;
        SelectedRoiY = 0;
        SelectedRoiW = 0;
        SelectedRoiH = 0;
    }

    // ==================== 私有：拍照 / 识别 ====================

    private bool CanStartCalibration() => !IsCalibrating;

    private bool CanSaveCalibration() => _calibrationResult is not null && _calibrationResult.IsValid && !IsCalibrating;

    private bool CanSaveTemplate() => HasSelectedRoi && !IsCalibrating;

    /// <summary>是否已有可识别的照片。</summary>
    private bool CanRecognize() => _lastFrame?.Mono8Data is not null && !IsCalibrating;

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

    /// <summary>把刚拍的帧显示到界面，并清空上一次的识别结果（等操作员点「识别」）。</summary>
    private async Task ShowFrameAsync(Mat mat, int width, int height)
    {
        var bitmap = WpfImageConverter.ToBitmapSource(mat);

        await _ui.InvokeAsync(() =>
        {
            PreviewImage = bitmap;
            PreviewWidth = width;
            PreviewHeight = height;

            // 新画面上的框选没有意义了（对应的是上一帧的内容）
            ClearRoiSelection();

            Mark1Found = false;
            Mark2Found = false;
            Mark1Text = "未识别";
            Mark2Text = "未识别";
            MatchScoreText = "--";
            AngleText = "--";
            DeltaText = "--";
            PreviewStatus = "已拍照，点击「识别」";
        });
    }

    /// <summary>识别两个 MARK 并计算纠偏（后台线程执行）：同一块搜索区域上跑两个模板，身份由直径区分。</summary>
    private (TemplateMatchResult? Mark1, TemplateMatchResult? Mark2, CorrectionResult? Correction) Recognize(Mat mat)
    {
        var searchRoi = ToRect(_searchRoi);
        var m1 = _matcher1.Match(mat, searchRoi, _matchThreshold);
        var m2 = _matcher2.Match(mat, searchRoi, _matchThreshold);

        CorrectionResult? corr = null;
        if (m1 is not null && m2 is not null && _calibrationResult is not null)
        {
            var template1 = new Point2f((float)_templateMark1U, (float)_templateMark1V);
            var template2 = new Point2f((float)_templateMark2U, (float)_templateMark2V);

            // 间距校验：与 VisionService 同口径，拦住"某个模板命中了干扰圆"的情况
            var expected = Distance(template1, template2);
            var actual = Distance(m1.Value.Center, m2.Value.Center);
            if (Math.Abs(actual - expected) > _markDistanceTolerance)
            {
                _log.LogWarning("两个 MARK 间距异常：实测 {Actual:F1}px 标称 {Expected:F1}px", actual, expected);
                return (m1, m2, null);
            }

            corr = _correctionService.Compute(
                m1.Value.Center, m2.Value.Center, template1, template2,
                _calibrationResult, _maxDeltaX, _maxDeltaY, _maxDeltaTheta);
        }

        return (m1, m2, corr);
    }

    /// <summary>把识别结果写入界面（必须在 UI 线程调用）。</summary>
    private void ApplyRecognition(
        (TemplateMatchResult? Mark1, TemplateMatchResult? Mark2, CorrectionResult? Correction) recognition)
    {
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
            // 按显示精度归零：标称姿态下 Δ 只剩浮点噪声（如 -1e-15），直接格式化会显示成 "-0.00"，观感像出错
            var showX = SnapForDisplay(corr.DeltaX, 3);
            var showY = SnapForDisplay(corr.DeltaY, 3);
            var showTheta = SnapForDisplay(corr.DeltaTheta, 2);

            AngleText = $"{showTheta:F2}°";
            DeltaText = $"ΔX {showX:F3} mm  ΔY {showY:F3} mm  Δθ {showTheta:F2}°";
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

        _searchRoi = vision.SearchRoi;
        _markDistanceTolerance = vision.MarkDistanceTolerancePx;
        _mark1Radius = vision.Mark1RadiusPx;
        _mark2Radius = vision.Mark2RadiusPx;
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

        LoadTemplate(vision.Template1, _matcher1);
        LoadTemplate(vision.Template2, _matcher2);

        EnsureSearchRoiUsable();
        ApplyRoiToUi();

        LoadActiveCalibration();
    }

    /// <summary>无激活配方时使用默认视觉参数（搜索区域与阈值取 VisionParameters 默认值）。</summary>
    private void LoadDefaultVisionParameters()
    {
        var vision = new VisionParameters();
        _searchRoi = vision.SearchRoi;
        _markDistanceTolerance = vision.MarkDistanceTolerancePx;
        _mark1Radius = vision.Mark1RadiusPx;
        _mark2Radius = vision.Mark2RadiusPx;
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

    /// <summary>把搜索区域写入叠加属性（UI 线程）。</summary>
    private void ApplyRoiToUi()
    {
        SearchRoiX = _searchRoi.X;
        SearchRoiY = _searchRoi.Y;
        SearchRoiW = _searchRoi.W;
        SearchRoiH = _searchRoi.H;
    }

    /// <summary>
    /// 校验搜索区域：小于「模板 + 20px」时按标称 MARK 位置重算并写回配方。
    /// 目的是消除「搜索区 = 模板尺寸 → 只有一个候选位置 → 坐标恒为区域中心」这种静默错误。
    /// </summary>
    private void EnsureSearchRoiUsable()
    {
        var templateSize = Math.Max(_matcher1.TemplateWidth, _matcher2.TemplateWidth);
        if (templateSize <= 0)
        {
            return;
        }

        var usable = _searchRoi.IsValid &&
                     _searchRoi.W >= templateSize + 20 &&
                     _searchRoi.H >= templateSize + 20;
        if (usable)
        {
            return;
        }

        var previous = _searchRoi;
        _searchRoi = VisionParameters.CreateNominalSearchRoi(
            _templateMark1U, _templateMark1V, _templateMark2U, _templateMark2V,
            VisionParameters.DefaultSearchMarginPx);

        _log.LogWarning("搜索区域过小（{Previous}，模板 {TemplateSize}px），已按标称位置重置为 {Current}",
                        previous, templateSize, _searchRoi);

        if (_activeRecipe is not null)
        {
            _activeRecipe.Parameters.Vision.SearchRoi = _searchRoi;
            _recipes.Update(_activeRecipe);
        }
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

    private void SaveMarkTemplate(int markIndex)
    {
        if (_selectedRoi is not { } roi)
        {
            _notifier.Warn("请先在图上框选 MARK 区域。");
            return;
        }

        // 模板必须从"当前显示的这一帧"裁，不能重新取图：重新取图会拿到另一帧，框选位置与画面就对不上了
        var frame = _lastFrame;
        if (frame?.Mono8Data is null)
        {
            _notifier.Warn("请先点击「拍照」获取图像，再框选并保存模板。");
            return;
        }

        try
        {
            using var mat = ImageConverter.ToMat(frame.Mono8Data, frame.Width, frame.Height);
            var rect = ToRect(roi);
            var nominalRadius = markIndex == 1 ? _mark1Radius : _mark2Radius;

            // 模板必须以 MARK 真实中心为中心：Match 返回的是"模板框中心"，
            // 框选时 MARK 一旦不居中，识别点就会跟着偏出圆心（且换模板后会引入真实偏差）。
            var cropRect = rect;
            if (MarkLocator.TryLocate(mat, rect, nominalRadius, out var markCenter, out var locateReason))
            {
                cropRect = CenterWindowOn(rect, markCenter, mat.Width, mat.Height);
                _log.LogInformation("MARK{Index} 圆心定位：({U:F2}, {V:F2})，裁剪窗口 {Rect}",
                                    markIndex, markCenter.X, markCenter.Y, cropRect);
            }
            else
            {
                _log.LogWarning("MARK{Index} 圆心定位失败（{Reason}），按框选区域直接裁剪", markIndex, locateReason);
                _notifier.Warn($"未能定位 MARK 圆心（{locateReason}），请把 MARK 框在框选区域正中后重新保存。");
            }

            var relativePath = markIndex == 1
                ? "templates/{recipe}/mark1.png"
                : "templates/{recipe}/mark2.png";
            var fullPath = ResolveTemplatePath(relativePath);

            ImageConverter.SaveRoiPng(mat, cropRect, fullPath);

            // 用刚保存的模板匹配找精确中心，作为模板基准坐标（与后续识别口径一致）
            var matcher = markIndex == 1 ? _matcher1 : _matcher2;
            matcher.LoadTemplate(fullPath);
            var match = matcher.Match(mat, new Rect(0, 0, frame.Width, frame.Height), 0.5);
            if (match is null)
            {
                _notifier.Warn("模板已保存，但在当前图中未找到对应 MARK，请重新框选更准确的区域。");
                return;
            }

            UpdateRecipeTemplate(markIndex, cropRect, relativePath, match.Value.Center);
            ClearRoiSelection();

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

    /// <summary>
    /// 更新激活配方的视觉参数（模板路径 / 模板基准坐标）并落库。
    /// 注意：**不改搜索区域**——搜索区域是「到哪找」，由标称位置决定；模板只决定「长什么样」。
    /// </summary>
    private void UpdateRecipeTemplate(int markIndex, Rect roi, string relativePath, Point2f center)
    {
        _ = roi;   // 框选框只用于裁模板，不再写入配方

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
            vision.Template1 = templatePath;
            vision.TemplateMark1U = center.X;
            vision.TemplateMark1V = center.Y;
            _templateMark1U = center.X;
            _templateMark1V = center.Y;
        }
        else
        {
            vision.Template2 = templatePath;
            vision.TemplateMark2U = center.X;
            vision.TemplateMark2V = center.Y;
            _templateMark2U = center.X;
            _templateMark2V = center.Y;
        }

        // 新模板尺寸可能比旧的大，顺手确认搜索区域仍然够用
        EnsureSearchRoiUsable();

        recipe.UpdatedBy = _auth.CurrentUser?.UserName ?? "";
        _recipes.Update(recipe);
        ApplyRoiToUi();
    }

    // ==================== 工具 ====================

    private static Rect ToRect(RoiRect roi) => new(roi.X, roi.Y, roi.W, roi.H);

    /// <summary>
    /// 按显示精度归零：小于末位一半的数值直接显示为 0。
    /// 目的是避免"标称姿态下 Δ 是浮点噪声"被格式化成 "-0.00" 这种看起来像故障的输出。
    /// </summary>
    /// <param name="value">原始数值。</param>
    /// <param name="decimals">显示小数位数。</param>
    private static double SnapForDisplay(double value, int decimals)
    {
        var threshold = 0.5 * Math.Pow(10, -decimals);
        return Math.Abs(value) < threshold ? 0.0 : value;
    }

    /// <summary>两点欧氏距离（像素）。</summary>
    private static double Distance(Point2f a, Point2f b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

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
