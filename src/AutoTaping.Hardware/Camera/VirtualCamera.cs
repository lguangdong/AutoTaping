// 职责：虚拟相机——优先从样本图目录随机取一帧灰度图，无样本图或标定态时生成带 MARK 的合成图
// 层：Hardware/Camera
// 依赖：ICamera（Core/Abstractions）、ILogger

using AutoTaping.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Hardware.Camera;

/// <summary>
/// <see cref="ICamera"/> 的虚拟实现。行为遵循 <c>docs/03-硬件对接-海康相机.md</c> 第 4 节：
/// 每次取图先尝试从「样本图提供者」随机取一帧真实感的模拟采样图（不同工件姿态）；
/// 无样本图可用、或处于「平台位置模拟」状态（九点标定需要确定性的像素-平台关系）时，
/// 退化为生成噪声底 + 两个实心圆 MARK 的合成图。两种模式都模拟 80–150ms 取图耗时。
/// </summary>
public sealed class VirtualCamera : ICamera
{
    /// <summary>默认图像宽度（像素）。</summary>
    private const int DefaultWidth = 1280;

    /// <summary>默认图像高度（像素）。</summary>
    private const int DefaultHeight = 1024;

    /// <summary>MARK1 基准 X（像素）。</summary>
    private const int DefaultMark1X = 400;

    /// <summary>MARK1 基准 Y（像素）。</summary>
    private const int DefaultMark1Y = 500;

    /// <summary>MARK2 基准 X（像素）。</summary>
    private const int DefaultMark2X = 880;

    /// <summary>MARK2 基准 Y（像素）。</summary>
    private const int DefaultMark2Y = 500;

    /// <summary>模拟取图耗时下限（ms）。</summary>
    private const int GrabDelayMinMs = 80;

    /// <summary>模拟取图耗时上限（ms）。</summary>
    private const int GrabDelayMaxMs = 150;

    /// <summary>底色灰度值。</summary>
    private const byte BackgroundGray = 40;

    /// <summary>MARK 灰度值。</summary>
    private const byte MarkGray = 255;

    /// <summary>实心圆 MARK1 半径（像素，大圆，直径 31）。</summary>
    private const int Mark1Radius = 15;

    /// <summary>实心圆 MARK2 半径（像素，小圆，直径 19）：与 MARK1 差 6px，靠直径区分身份。</summary>
    private const int Mark2Radius = 9;

    /// <summary>模拟标定映射的基准平台 X（mm），对应配方默认拍照位 X（见 06 文档位置参数）。</summary>
    private const double SimCalibBasePlatformX = 150.0;

    /// <summary>模拟标定映射的基准平台 Y（mm），对应配方默认拍照位 Y。</summary>
    private const double SimCalibBasePlatformY = 200.0;

    /// <summary>模拟标定映射的比例（px/mm）：平台每移动 1mm，MARK 在图像中移动 4px。</summary>
    private const double SimCalibScalePxPerMm = 4.0;

    private readonly int _width;
    private readonly int _height;
    private readonly int _mark1X;
    private readonly int _mark1Y;
    private readonly int _mark2X;
    private readonly int _mark2Y;

    private readonly Random _random = new();
    private readonly ILogger<VirtualCamera> _log;
    private readonly object _sync = new();

    /// <summary>样本图提供者（返回一帧 Mono8 数据与尺寸）；<see langword="null"/> 表示只用合成图。</summary>
    private readonly Func<(byte[] Mono8, int Width, int Height)?>? _sampleProvider;

    private bool _initialized;
    private bool _isGrabbing;
    private bool _hasPlatformPosition;
    private double _platformX;
    private double _platformY;
    private bool _disposed;

    /// <summary>构造虚拟相机（1280×1024，MARK 位于 (400,500) 与 (880,500)）。</summary>
    /// <param name="log">日志。</param>
    /// <param name="sampleProvider">样本图提供者（可空）；提供时优先返回样本图而非合成图。</param>
    public VirtualCamera(ILogger<VirtualCamera> log,
                         Func<(byte[] Mono8, int Width, int Height)?>? sampleProvider = null)
        : this(log, DefaultWidth, DefaultHeight, DefaultMark1X, DefaultMark1Y, DefaultMark2X, DefaultMark2Y,
               sampleProvider)
    {
    }

    /// <summary>构造虚拟相机。</summary>
    /// <param name="log">日志。</param>
    /// <param name="width">图像宽度（像素）。</param>
    /// <param name="height">图像高度（像素）。</param>
    /// <param name="mark1X">MARK1 基准 X。</param>
    /// <param name="mark1Y">MARK1 基准 Y。</param>
    /// <param name="mark2X">MARK2 基准 X。</param>
    /// <param name="mark2Y">MARK2 基准 Y。</param>
    /// <param name="sampleProvider">样本图提供者（可空）。</param>
    public VirtualCamera(ILogger<VirtualCamera> log, int width, int height,
                         int mark1X, int mark1Y, int mark2X, int mark2Y,
                         Func<(byte[] Mono8, int Width, int Height)?>? sampleProvider = null)
    {
        _log = log;
        _width = width;
        _height = height;
        _mark1X = mark1X;
        _mark1Y = mark1Y;
        _mark2X = mark2X;
        _mark2Y = mark2Y;
        _sampleProvider = sampleProvider;
    }

    /// <inheritdoc />
    public event EventHandler<CameraFaultEventArgs>? Fault;

    /// <inheritdoc />
    public bool Initialize()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }

            _initialized = true;
            _log.LogInformation("虚拟相机初始化成功（样本图 {HasSamples}，合成图 {Width}x{Height}）",
                                _sampleProvider is not null, _width, _height);
            return true;
        }
    }

    /// <inheritdoc />
    public bool IsConnected
    {
        get { lock (_sync) { return _initialized && !_disposed; } }
    }

    /// <inheritdoc />
    public bool IsGrabbing
    {
        get { lock (_sync) { return _isGrabbing; } }
    }

    /// <inheritdoc />
    public bool StartGrabbing()
    {
        lock (_sync)
        {
            if (!_initialized || _disposed)
            {
                return false;
            }

            _isGrabbing = true;
            return true;
        }
    }

    /// <inheritdoc />
    public void StopGrabbing()
    {
        lock (_sync) { _isGrabbing = false; }
    }

    /// <inheritdoc />
    public async Task<CaptureResult> TriggerAndGrabAsync(int timeoutMs, CancellationToken ct)
    {
        int delay;
        lock (_sync)
        {
            if (_disposed)
            {
                return Fail("相机已释放");
            }

            if (!_initialized)
            {
                return Fail("相机未连接");
            }

            if (!_isGrabbing)
            {
                return Fail("未开始采集");
            }

            delay = _random.Next(GrabDelayMinMs, GrabDelayMaxMs + 1);
        }

        await Task.Delay(delay, ct);

        // 样本图模式：每次随机取一张模拟采样图（标定态除外——标定要求像素与平台位置确定性对应）
        if (_sampleProvider is not null && !HasPlatformPosition())
        {
            var sample = _sampleProvider();
            if (sample is { } s && s.Mono8.Length >= s.Width * s.Height)
            {
                return new CaptureResult
                {
                    Success = true,
                    Mono8Data = s.Mono8,
                    Width = s.Width,
                    Height = s.Height,
                    Timestamp = DateTime.Now,
                };
            }

            _log.LogWarning("样本图读取失败，本帧退化为合成图");
        }

        lock (_sync)
        {
            var data = GenerateFrame();
            return new CaptureResult
            {
                Success = true,
                Mono8Data = data,
                Width = _width,
                Height = _height,
                Timestamp = DateTime.Now,
            };
        }
    }

    /// <inheritdoc />
    public void SoftwareTrigger()
    {
        // 软触发在 TriggerAndGrabAsync 内已隐含，单独触发无需额外动作
    }

    /// <inheritdoc />
    public bool SetExposure(double microseconds)
    {
        lock (_sync)
        {
            _log.LogDebug("虚拟相机设置曝光 {Exposure}μs", microseconds);
            return _initialized && !_disposed;
        }
    }

    /// <inheritdoc />
    public bool SetGain(double gain)
    {
        lock (_sync)
        {
            _log.LogDebug("虚拟相机设置增益 {Gain}dB", gain);
            return _initialized && !_disposed;
        }
    }

    /// <summary>
    /// 设置"当前平台位置"，开启「平台移动 → MARK 像素移动」的模拟（供 S5 九点标定）。
    /// 真实相机下工件随平台移动，相机固定；虚拟相机没有真实工件，因此用平台位置反推 MARK 像素坐标，
    /// 让九点标定在无硬件时也能拟合出真实反映「像素 → 平台」映射的仿射矩阵。
    /// 该状态下取图一律走合成图（样本图姿态随机，无法与平台位置对应）。
    /// </summary>
    /// <param name="x">平台 X（mm）。</param>
    /// <param name="y">平台 Y（mm）。</param>
    public void SetPlatformPosition(double x, double y)
    {
        lock (_sync)
        {
            _platformX = x;
            _platformY = y;
            _hasPlatformPosition = true;
        }
    }

    /// <summary>退出「平台位置模拟」状态，恢复按样本图出帧（标定结束后调用）。</summary>
    public void ClearPlatformPosition()
    {
        lock (_sync)
        {
            _hasPlatformPosition = false;
        }
    }

    /// <summary>模拟相机掉线（触发 <see cref="Fault"/> 事件），供演示报警流程（03 文档 4.1）。</summary>
    public void Disconnect()
    {
        CameraFaultEventArgs? args = null;

        lock (_sync)
        {
            if (!_initialized || _disposed)
            {
                return;
            }

            _initialized = false;
            _isGrabbing = false;
            args = new CameraFaultEventArgs { Message = "相机通讯中断", ErrorCode = -1 };
        }

        _log.LogWarning("虚拟相机模拟掉线");
        Fault?.Invoke(this, args);
    }

    /// <summary>模拟相机重连。</summary>
    public void Reconnect()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _initialized = true;
        }

        _log.LogInformation("虚拟相机模拟重连");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _isGrabbing = false;
        }
    }

    /// <summary>是否处于「平台位置模拟」状态（标定中）。</summary>
    private bool HasPlatformPosition()
    {
        lock (_sync)
        {
            return _hasPlatformPosition;
        }
    }

    /// <summary>
    /// 计算两个 MARK 的最终像素位置 = 基准 + 平台位移换算。
    /// 平台 X 增大 → MARK 向右（u 增大）；平台 Y 增大 → MARK 向上（v 减小，像素纵轴向下）。
    /// 本方法只在调用方持锁时被调用（GenerateFrame 位于 TriggerAndGrabAsync 的锁块内）。
    /// </summary>
    private (int Mark1X, int Mark1Y, int Mark2X, int Mark2Y) ComputeMarkPositions()
    {
        int platformDeltaU = 0;
        int platformDeltaV = 0;

        if (_hasPlatformPosition)
        {
            platformDeltaU = (int)Math.Round((_platformX - SimCalibBasePlatformX) * SimCalibScalePxPerMm);
            platformDeltaV = (int)Math.Round(-(_platformY - SimCalibBasePlatformY) * SimCalibScalePxPerMm);
        }

        return (_mark1X + platformDeltaU,
                _mark1Y + platformDeltaV,
                _mark2X + platformDeltaU,
                _mark2Y + platformDeltaV);
    }

    /// <summary>生成一帧合成灰度图（噪声底 + 两个实心圆 MARK）。</summary>
    private byte[] GenerateFrame()
    {
        var buffer = new byte[_width * _height];
        Array.Fill(buffer, BackgroundGray);

        var (mark1X, mark1Y, mark2X, mark2Y) = ComputeMarkPositions();

        DrawFilledCircle(buffer, mark1X, mark1Y, Mark1Radius);
        DrawFilledCircle(buffer, mark2X, mark2Y, Mark2Radius);

        // 轻微噪声，更接近真实图像（03 文档 4.2）
        var noiseCount = buffer.Length / 20;
        for (var n = 0; n < noiseCount; n++)
        {
            var idx = _random.Next(buffer.Length);
            var delta = _random.Next(-3, 4);
            buffer[idx] = (byte)Math.Clamp(buffer[idx] + delta, 0, 255);
        }

        return buffer;
    }

    /// <summary>
    /// 在指定中心画一个实心圆 MARK。逐像素按「到圆心距离 ≤ 半径」判定，
    /// 不做抗锯齿，保持与 <c>VisionService</c> 的合成模板完全同一套像素判定（边缘一致才好匹配）。
    /// </summary>
    private void DrawFilledCircle(byte[] buffer, int cx, int cy, int radius)
    {
        var radiusSquared = radius * radius;

        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if ((dx * dx) + (dy * dy) <= radiusSquared)
                {
                    SetPixel(buffer, cx + dx, cy + dy, MarkGray);
                }
            }
        }
    }

    /// <summary>写单个像素，越界忽略。</summary>
    private void SetPixel(byte[] buffer, int x, int y, byte value)
    {
        if (x < 0 || x >= _width || y < 0 || y >= _height)
        {
            return;
        }

        buffer[y * _width + x] = value;
    }

    /// <summary>构造失败结果。</summary>
    private static CaptureResult Fail(string message) => new() { Success = false, ErrorMessage = message };
}
