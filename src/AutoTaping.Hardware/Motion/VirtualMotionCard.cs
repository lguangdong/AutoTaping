// 职责：虚拟运控卡——4 轴模拟运动（线性推进 / 软限位 / 急停 / 回零），供无硬件开发、演示与单元测试
// 层：Hardware/Motion
// 依赖：IMotionCard（Core/Abstractions）、HardwareException（Core/Exceptions）、ILogger

using System.Diagnostics;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Hardware.Motion;

/// <summary>
/// <see cref="IMotionCard"/> 的虚拟实现。行为遵循 <c>docs/02-硬件对接-雷赛运控卡.md</c> 第 5 节：
/// 位置按真实时间线性推进、超软限位拒绝并触发 <see cref="AxisFault"/>、急停后拒绝一切运动直到复位。
/// </summary>
public sealed class VirtualMotionCard : IMotionCard
{
    /// <summary>虚拟设备名（用于 HardwareException 与日志）。</summary>
    private const string DeviceName = "虚拟运控卡";

    /// <summary>位置推进周期（ms）。02 文档 5.1：每 20ms 更新一次位置。</summary>
    private const int TickIntervalMs = 20;

    /// <summary>回零模拟耗时（s）。02 文档 5.1：模拟 1.5s 后位置归 0。</summary>
    private const double HomeDurationSec = 1.5;

    /// <summary>最短运动耗时（s）。02 文档 5.1：最小 20ms。</summary>
    private const double MinMoveDurationSec = 0.02;

    /// <summary>MoveAndWaitAsync 到位轮询间隔（ms）。02 文档 4：10ms 轮询足够。</summary>
    private const int PollIntervalMs = 10;

    /// <summary>默认软限位下限，按 AxisId 顺序 X/Y/Z/R（与 appsettings.json 示例一致，测试可直接使用）。</summary>
    private static readonly double[] DefaultMinLimits = { 0, 0, 0, -180 };

    /// <summary>默认软限位上限，按 AxisId 顺序 X/Y/Z/R（与 appsettings.json 示例一致，测试可直接使用）。</summary>
    private static readonly double[] DefaultMaxLimits = { 400, 400, 60, 180 };

    private readonly object _sync = new();
    private readonly ILogger<VirtualMotionCard> _log;

    private readonly double[] _pos = new double[4];
    private readonly double[] _target = new double[4];
    private readonly double[] _speed = new double[4];
    private readonly double[] _moveStartPos = new double[4];
    private readonly long[] _moveStartTick = new long[4];
    private readonly double[] _moveDurationSec = new double[4];
    private readonly bool[] _moving = new bool[4];
    private readonly bool[] _jogging = new bool[4];
    private readonly int[] _jogDir = new int[4];
    private readonly bool[] _homing = new bool[4];
    private readonly long[] _homeStartTick = new long[4];
    private readonly bool[] _homed = new bool[4];
    private readonly int[] _stopReason = new int[4];
    private readonly bool[] _estop = new bool[4];
    private readonly bool[] _softLimitEnabled = new bool[4];
    private readonly double[] _softLimitMin = new double[4];
    private readonly double[] _softLimitMax = new double[4];

    private readonly Timer _timer;
    private bool _initialized;
    private bool _disposed;

    /// <summary>构造虚拟运控卡（使用默认软限位）。</summary>
    /// <param name="log">日志。</param>
    public VirtualMotionCard(ILogger<VirtualMotionCard> log)
        : this(log, DefaultMinLimits, DefaultMaxLimits)
    {
    }

    /// <summary>构造虚拟运控卡。</summary>
    /// <param name="log">日志。</param>
    /// <param name="minLimits">各轴软限位下限，按 <see cref="AxisId"/> 顺序（长度 4）。</param>
    /// <param name="maxLimits">各轴软限位上限，按 <see cref="AxisId"/> 顺序（长度 4）。</param>
    public VirtualMotionCard(ILogger<VirtualMotionCard> log, double[] minLimits, double[] maxLimits)
    {
        _log = log;

        for (var i = 0; i < 4; i++)
        {
            _softLimitEnabled[i] = true;
            _softLimitMin[i] = minLimits[i];
            _softLimitMax[i] = maxLimits[i];
        }

        _timer = new Timer(Tick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <inheritdoc />
    public event EventHandler<AxisFaultEventArgs>? AxisFault;

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
            _timer.Change(0, TickIntervalMs);
            _log.LogInformation("虚拟运控卡初始化成功（4 轴模拟）");
            return true;
        }
    }

    /// <inheritdoc />
    public bool IsConnected
    {
        get { lock (_sync) { return _initialized && !_disposed; } }
    }

    /// <inheritdoc />
    public double GetPosition(AxisId axis)
    {
        lock (_sync) { return _pos[(int)axis]; }
    }

    /// <inheritdoc />
    public bool IsInPosition(AxisId axis)
    {
        lock (_sync)
        {
            var i = (int)axis;
            return !_moving[i] && !_jogging[i] && !_homing[i] && _stopReason[i] == MotionErrorCodes.Normal;
        }
    }

    /// <inheritdoc />
    public int GetStopReason(AxisId axis)
    {
        lock (_sync) { return _stopReason[(int)axis]; }
    }

    /// <inheritdoc />
    public void Home(AxisId axis)
    {
        lock (_sync)
        {
            EnsureMovable(axis);
            var i = (int)axis;
            _moving[i] = false;
            _jogging[i] = false;
            _homing[i] = true;
            _homeStartTick[i] = Environment.TickCount64;
            _stopReason[i] = MotionErrorCodes.Normal;
            _log.LogDebug("轴 {Axis} 开始回零", axis);
        }
    }

    /// <inheritdoc />
    public bool IsHomeCompleted(AxisId axis)
    {
        lock (_sync) { return _homed[(int)axis]; }
    }

    /// <inheritdoc />
    public void MoveAbsolute(AxisId axis, double position, double speed)
    {
        MoveTo(axis, position, speed);
    }

    /// <inheritdoc />
    public void MoveRelative(AxisId axis, double distance, double speed)
    {
        double target;
        lock (_sync) { target = _pos[(int)axis] + distance; }
        MoveTo(axis, target, speed);
    }

    /// <inheritdoc />
    public void JogStart(AxisId axis, int direction, double speed)
    {
        lock (_sync)
        {
            EnsureMovable(axis);
            var i = (int)axis;
            _moving[i] = false;
            _homing[i] = false;
            _jogging[i] = true;
            _jogDir[i] = direction >= 0 ? 1 : -1;
            _speed[i] = speed;
            _stopReason[i] = MotionErrorCodes.Normal;
        }
    }

    /// <inheritdoc />
    public void Stop(AxisId axis, bool emergency)
    {
        lock (_sync)
        {
            var i = (int)axis;
            _moving[i] = false;
            _jogging[i] = false;
            _homing[i] = false;

            if (emergency)
            {
                _estop[i] = true;
                _stopReason[i] = MotionErrorCodes.EmergencyStop;
            }
            else
            {
                _stopReason[i] = MotionErrorCodes.Normal;
            }

            _log.LogDebug("轴 {Axis} 停止（{Mode}）", axis, emergency ? "急停" : "减速停");
        }
    }

    /// <inheritdoc />
    public void EmergencyStopAll()
    {
        lock (_sync)
        {
            for (var i = 0; i < 4; i++)
            {
                _moving[i] = false;
                _jogging[i] = false;
                _homing[i] = false;
                _estop[i] = true;
                _stopReason[i] = MotionErrorCodes.EmergencyStop;
            }

            _log.LogWarning("全轴急停");
        }
    }

    /// <inheritdoc />
    public void ResetAxis(AxisId axis)
    {
        lock (_sync)
        {
            var i = (int)axis;
            _estop[i] = false;
            _stopReason[i] = MotionErrorCodes.Normal;
            _log.LogInformation("轴 {Axis} 已复位", axis);
        }
    }

    /// <inheritdoc />
    public void SetSoftLimit(AxisId axis, bool enable, double min, double max)
    {
        lock (_sync)
        {
            var i = (int)axis;
            _softLimitEnabled[i] = enable;
            _softLimitMin[i] = min;
            _softLimitMax[i] = max;
        }
    }

    /// <inheritdoc />
    public async Task<bool> MoveAndWaitAsync(AxisId axis, double position, double speed, int timeoutMs, CancellationToken ct)
    {
        MoveAbsolute(axis, position, speed);

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            if (IsInPosition(axis))
            {
                return true;
            }

            if (GetStopReason(axis) != MotionErrorCodes.Normal)
            {
                return false;
            }

            await Task.Delay(PollIntervalMs, ct);
        }

        _log.LogWarning("轴 {Axis} 运动超时 {Timeout}ms", axis, timeoutMs);
        Stop(axis, emergency: true);
        return false;
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
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _timer.Dispose();
        }
    }

    /// <summary>绝对定位核心：校验急停与软限位后启动运动，超软限位时触发 AxisFault 并抛异常。</summary>
    private void MoveTo(AxisId axis, double target, double speed)
    {
        AxisFaultEventArgs? fault = null;

        lock (_sync)
        {
            EnsureMovable(axis);

            var i = (int)axis;
            if (_softLimitEnabled[i] && (target < _softLimitMin[i] || target > _softLimitMax[i]))
            {
                _stopReason[i] = MotionErrorCodes.SoftLimit;
                fault = new AxisFaultEventArgs
                {
                    Axis = axis,
                    Reason = MotionErrorCodes.SoftLimit,
                    Message = $"轴 {axis} 目标 {target:F2} 超出软限位 [{_softLimitMin[i]:F2}, {_softLimitMax[i]:F2}]",
                };
            }
            else
            {
                StartMoveLocked(axis, target, speed);
            }
        }

        if (fault is not null)
        {
            _log.LogWarning("轴 {Axis} 运动被拒绝：{Message}", axis, fault.Message);
            AxisFault?.Invoke(this, fault);
            throw new HardwareException(DeviceName, fault.Reason, fault.Message);
        }
    }

    /// <summary>在锁内启动一次绝对定位。</summary>
    private void StartMoveLocked(AxisId axis, double target, double speed)
    {
        var i = (int)axis;
        _moving[i] = true;
        _jogging[i] = false;
        _homing[i] = false;
        _moveStartPos[i] = _pos[i];
        _moveStartTick[i] = Environment.TickCount64;
        _target[i] = target;
        _speed[i] = speed;
        _stopReason[i] = MotionErrorCodes.Normal;

        var distance = Math.Abs(target - _pos[i]);
        _moveDurationSec[i] = Math.Max(distance / speed, MinMoveDurationSec);

        _log.LogDebug("轴 {Axis} 定位 目标={Target:F2} 速度={Speed:F1} 预计耗时={Duration:F2}s",
                      axis, target, speed, _moveDurationSec[i]);
    }

    /// <summary>急停锁校验：急停未复位前拒绝一切运动。</summary>
    private void EnsureMovable(AxisId axis)
    {
        if (_estop[(int)axis])
        {
            throw new HardwareException(DeviceName, MotionErrorCodes.EmergencyStop,
                                        $"轴 {axis} 处于急停状态，请先复位");
        }
    }

    /// <summary>定时器回调：推进运动中的轴、完成回零、推进点动并处理软限位边界。</summary>
    private void Tick(object? _)
    {
        lock (_sync)
        {
            if (_disposed || !_initialized)
            {
                return;
            }

            var now = Environment.TickCount64;
            for (var i = 0; i < 4; i++)
            {
                if (_homing[i])
                {
                    if ((now - _homeStartTick[i]) / 1000.0 >= HomeDurationSec)
                    {
                        _homing[i] = false;
                        _pos[i] = 0;
                        _homed[i] = true;
                        _stopReason[i] = MotionErrorCodes.Normal;
                    }

                    continue;
                }

                if (_moving[i])
                {
                    var elapsedSec = (now - _moveStartTick[i]) / 1000.0;
                    if (elapsedSec >= _moveDurationSec[i])
                    {
                        _pos[i] = _target[i];
                        _moving[i] = false;
                        _stopReason[i] = MotionErrorCodes.Normal;
                    }
                    else
                    {
                        _pos[i] = _moveStartPos[i] + Math.Sign(_target[i] - _moveStartPos[i]) * _speed[i] * elapsedSec;
                    }

                    continue;
                }

                if (_jogging[i])
                {
                    var step = _speed[i] * TickIntervalMs / 1000.0;
                    var next = _pos[i] + _jogDir[i] * step;

                    if (_softLimitEnabled[i] && (next > _softLimitMax[i] || next < _softLimitMin[i]))
                    {
                        _pos[i] = _jogDir[i] > 0 ? _softLimitMax[i] : _softLimitMin[i];
                        _jogging[i] = false;
                        _stopReason[i] = MotionErrorCodes.Limit;
                    }
                    else
                    {
                        _pos[i] = next;
                    }
                }
            }
        }
    }
}
