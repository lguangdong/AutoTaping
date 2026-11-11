// 职责：虚拟相机——生成带 MARK 的合成灰度图并按 ±5px 注入随机偏移，供无硬件开发与视觉纠偏演示
// 层：Hardware/Camera
// 依赖：ICamera（Core/Abstractions）、ILogger

using AutoTaping.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Hardware.Camera;

/// <summary>
/// <see cref="ICamera"/> 的虚拟实现。行为遵循 <c>docs/03-硬件对接-海康相机.md</c> 第 4 节：
/// 生成噪声底 + 两个十字 MARK 的合成图，每次取图随机平移 MARK ±5px，模拟 80–150ms 取图耗时。
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

    /// <summary>随机偏移最大值（像素），±5px（03 文档 4.1）。</summary>
    private const int MaxRandomOffset = 5;

    /// <summary>模拟取图耗时下限（ms）。</summary>
    private const int GrabDelayMinMs = 80;

    /// <summary>模拟取图耗时上限（ms）。</summary>
    private const int GrabDelayMaxMs = 150;

    /// <summary>底色灰度值。</summary>
    private const byte BackgroundGray = 40;

    /// <summary>MARK 灰度值。</summary>
    private const byte MarkGray = 255;

    /// <summary>十字 MARK 半臂长（像素）。</summary>
    private const int MarkHalfSize = 20;

    /// <summary>十字 MARK 线宽（像素）。</summary>
    private const int MarkThickness = 2;

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

    private bool _initialized;
    private bool _isGrabbing;
    private bool _hasInjectedOffset;
    private int _injectedOffsetX;
    private int _injectedOffsetY;
    private bool _hasPlatformPosition;
    private double _platformX;
    private double _platformY;
    private bool _disposed;

    /// <summary>构造虚拟相机（1280×1024，MARK 位于 (400,500) 与 (880,500)）。</summary>
    /// <param name="log">日志。</param>
    public VirtualCamera(ILogger<VirtualCamera> log)
        : this(log, DefaultWidth, DefaultHeight, DefaultMark1X, DefaultMark1Y, DefaultMark2X, DefaultMark2Y)
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
    public VirtualCamera(ILogger<VirtualCamera> log, int width, int height,
                         int mark1X, int mark1Y, int mark2X, int mark2Y)
    {
        _log = log;
        _width = width;
        _height = height;
        _mark1X = mark1X;
        _mark1Y = mark1Y;
        _mark2X = mark2X;
        _mark2Y = mark2Y;
    }

    /// <inheritdoc />
    public event EventHandler<CameraFaultEventArgs>? Fault;

    /// <summary>最近一帧实际注入的 MARK 偏移 X（像素），供测试与演示验证纠偏是否在起作用。</summary>
    public int LastOffsetX { get; private set; }

    /// <summary>最近一帧实际注入的 MARK 偏移 Y（像素）。</summary>
    public int LastOffsetY { get; private set; }

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
            _log.LogInformation("虚拟相机初始化成功（{Width}x{Height} 合成图）", _width, _height);
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

        lock (_sync)
        {
            var (dx, dy) = NextOffset();
            LastOffsetX = dx;
            LastOffsetY = dy;

            var data = GenerateFrame(dx, dy);
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
    /// 注入固定偏移（供 S5 视觉纠偏演示用，例如注入 ±3px 验证纠偏计算）。
    /// 注入后每帧使用固定偏移；不注入时使用随机 ±5px。
    /// </summary>
    /// <param name="dx">MARK 偏移 X（像素）。</param>
    /// <param name="dy">MARK 偏移 Y（像素）。</param>
    public void InjectOffset(int dx, int dy)
    {
        lock (_sync)
        {
            _hasInjectedOffset = true;
            _injectedOffsetX = dx;
            _injectedOffsetY = dy;
        }
    }

    /// <summary>
    /// 设置"当前平台位置"，开启「平台移动 → MARK 像素移动」的模拟（供 S5 九点标定）。
    /// 真实相机下工件随平台移动，相机固定；虚拟相机没有真实工件，因此用平台位置反推 MARK 像素坐标，
    /// 让九点标定在无硬件时也能拟合出真实反映「像素 → 平台」映射的仿射矩阵。
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

    /// <summary>取下一次偏移：注入固定偏移优先，否则随机 ±5px。</summary>
    private (int X, int Y) NextOffset()
    {
        if (_hasInjectedOffset)
        {
            return (_injectedOffsetX, _injectedOffsetY);
        }

        return (_random.Next(-MaxRandomOffset, MaxRandomOffset + 1),
                _random.Next(-MaxRandomOffset, MaxRandomOffset + 1));
    }

    /// <summary>
    /// 计算两个 MARK 的最终像素位置 = 基准 + 平台位移换算 + 注入偏移。
    /// 平台 X 增大 → MARK 向右（u 增大）；平台 Y 增大 → MARK 向上（v 减小，像素纵轴向下）。
    /// 本方法只在调用方持锁时被调用（GenerateFrame 位于 TriggerAndGrabAsync 的锁块内）。
    /// </summary>
    private (int Mark1X, int Mark1Y, int Mark2X, int Mark2Y) ComputeMarkPositions(int offsetX, int offsetY)
    {
        int platformDeltaU = 0;
        int platformDeltaV = 0;

        if (_hasPlatformPosition)
        {
            platformDeltaU = (int)Math.Round((_platformX - SimCalibBasePlatformX) * SimCalibScalePxPerMm);
            platformDeltaV = (int)Math.Round(-(_platformY - SimCalibBasePlatformY) * SimCalibScalePxPerMm);
        }

        return (_mark1X + platformDeltaU + offsetX,
                _mark1Y + platformDeltaV + offsetY,
                _mark2X + platformDeltaU + offsetX,
                _mark2Y + platformDeltaV + offsetY);
    }

    /// <summary>生成一帧合成灰度图（噪声底 + 两个带偏移的十字 MARK）。</summary>
    private byte[] GenerateFrame(int offsetX, int offsetY)
    {
        var buffer = new byte[_width * _height];
        Array.Fill(buffer, BackgroundGray);

        var (mark1X, mark1Y, mark2X, mark2Y) = ComputeMarkPositions(offsetX, offsetY);

        DrawCross(buffer, mark1X, mark1Y);
        DrawCross(buffer, mark2X, mark2Y);

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

    /// <summary>在指定中心画一个十字 MARK。</summary>
    private void DrawCross(byte[] buffer, int cx, int cy)
    {
        for (var d = -MarkHalfSize; d <= MarkHalfSize; d++)
        {
            for (var t = -MarkThickness; t <= MarkThickness; t++)
            {
                SetPixel(buffer, cx + t, cy + d, MarkGray);   // 竖线
                SetPixel(buffer, cx + d, cy + t, MarkGray);   // 横线
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
