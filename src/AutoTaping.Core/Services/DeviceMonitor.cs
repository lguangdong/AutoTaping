// 职责：设备采集线程实现——System.Threading.Timer 每 200ms 读取 4 轴状态与 DI/DO，整体替换快照
// 层：Core/Services
// 依赖：IMotionCard、IIoService、IClock（Core/Abstractions）、DeviceSnapshot（Core/Models）、ILogger

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Core.Services;

/// <summary>
/// <see cref="IDeviceMonitor"/> 的默认实现。用 <see cref="Timer"/>（不是 while+sleep，01-架构设计 第 3 节），
/// 每 200ms 读一次硬件并把结果整体替换为新快照；单次采集失败只记日志，线程继续运行（01-架构设计 第 6 节）。
/// </summary>
public sealed class DeviceMonitor : IDeviceMonitor
{
    /// <summary>采集周期（ms）。01-架构设计 第 3 节与 11-开发任务分解 S4 交付物均为 200ms。</summary>
    private const int PollIntervalMs = 200;

    /// <summary>连续失败时每隔多少次补记一条日志，避免一次掉线把日志刷满。</summary>
    private const int FailureLogInterval = 50;

    /// <summary>需要采集的轴（顺序与 <see cref="AxisId"/> 一致）。</summary>
    private static readonly AxisId[] Axes = { AxisId.X, AxisId.Y, AxisId.Z, AxisId.R };

    private readonly IMotionCard _motion;
    private readonly IIoService _io;
    private readonly ICamera _camera;
    private readonly IClock _clock;
    private readonly ILogger<DeviceMonitor> _log;
    private readonly Timer _timer;

    /// <summary>最近一帧快照；整体替换引用，故 UI 线程读到的永远是完整的一帧。</summary>
    private volatile DeviceSnapshot _current = DeviceSnapshot.Empty;

    private int _consecutiveFailures;
    private volatile bool _disposed;

    /// <summary>构造采集器（尚未开始采集，需调用 <see cref="Start"/>）。</summary>
    /// <param name="motion">运控卡抽象。</param>
    /// <param name="io">IO 服务抽象。</param>
    /// <param name="camera">相机抽象（采集连接状态，供互锁判定）。</param>
    /// <param name="clock">时钟（快照时间戳）。</param>
    /// <param name="log">日志。</param>
    public DeviceMonitor(IMotionCard motion, IIoService io, ICamera camera, IClock clock, ILogger<DeviceMonitor> log)
    {
        _motion = motion;
        _io = io;
        _camera = camera;
        _clock = clock;
        _log = log;
        _timer = new Timer(Tick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <inheritdoc />
    public DeviceSnapshot Current => _current;

    /// <inheritdoc />
    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        _timer.Change(0, PollIntervalMs);
        _log.LogInformation("设备采集线程已启动，周期 {Interval}ms", PollIntervalMs);
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _log.LogInformation("设备采集线程已停止");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _timer.Dispose();
    }

    /// <summary>定时器回调：采集一帧。任何异常都不得让线程死掉，否则界面会永久停在旧数据上。</summary>
    private void Tick(object? state)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _current = ReadSnapshot();
            _consecutiveFailures = 0;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;

            // 首次与每 50 次记录一次：既能看到故障开始，又不会因为 200ms 周期把日志写爆
            if (_consecutiveFailures == 1 || _consecutiveFailures % FailureLogInterval == 0)
            {
                _log.LogError(ex, "采集设备状态失败（连续第 {Count} 次），继续沿用上一帧快照", _consecutiveFailures);
            }
        }
    }

    /// <summary>读取一次全部状态并打包成不可变快照。</summary>
    private DeviceSnapshot ReadSnapshot()
    {
        var positions = new double[4];
        var inPosition = new bool[4];
        var homed = new bool[4];
        var stopReasons = new int[4];

        foreach (var axis in Axes)
        {
            var index = (int)axis;
            positions[index] = _motion.GetPosition(axis);
            inPosition[index] = _motion.IsInPosition(axis);
            homed[index] = _motion.IsHomeCompleted(axis);
            stopReasons[index] = _motion.GetStopReason(axis);
        }

        var di = _io.ReadAllDi();
        var doStates = _io.ReadAllDo();

        var forced = new List<string>();
        foreach (var point in doStates.Keys)
        {
            if (_io.IsForced(point))
            {
                forced.Add(point);
            }
        }

        return new DeviceSnapshot
        {
            AxisPositions = positions,
            AxisInPosition = inPosition,
            AxisHomed = homed,
            AxisStopReasons = stopReasons,
            MotionConnected = _motion.IsConnected,
            CameraConnected = _camera.IsConnected,
            DiStates = di,
            DoStates = doStates,
            ForcedPoints = forced,
            Timestamp = _clock.Now,
        };
    }
}
