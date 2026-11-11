// 职责：雷赛运控卡的真实 P/Invoke 实现——初始化/运动/回零/软限位/急停，与 VirtualMotionCard 互为替代
// 层：Hardware/Motion
// 依赖：LTDMC.dll（经 Native/LeadshineApi）、IMotionCard/HardwareException（Core）、ILogger

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Exceptions;
using AutoTaping.Hardware.Motion.Native;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Hardware.Motion;

/// <summary>
/// <see cref="IMotionCard"/> 的真实实现，基于雷赛 <c>LTDMC.dll</c>（DMC2410 系列，02 文档第 3/4 节）。
/// 关键约束（02 文档第 4/7 节）：
/// - DLL 非线程安全，所有调用经共享 <see cref="SemaphoreSlim"/> 串行化（与 <see cref="Io.MotionCardIoService"/> 共用同一把锁）；
/// - 无卡时 <see cref="Initialize"/> 返回 <see langword="false"/> 且不抛异常，由上层降级为 Virtual 并报 AL105；
/// - 任何 <see cref="Stop"/> 后 500ms 内拒绝新运动（02 文档 4.3 第 4 条）。
/// </summary>
public sealed class LeadshineMotionCard : IMotionCard
{
    /// <summary>设备名（HardwareException 与日志用）。</summary>
    private const string DeviceName = "雷赛运控卡";

    /// <summary>Stop 后禁止新运动的锁定时长（ms），02 文档 4.3 第 4 条。</summary>
    private const int StopLockoutMs = 500;

    /// <summary>MoveAndWaitAsync 到位轮询间隔（ms）。</summary>
    private const int PollIntervalMs = 10;

    private readonly ushort _cardNo;
    private readonly LeadshineAxisOptions[] _axes;
    private readonly SemaphoreSlim _dllLock;
    private readonly ILogger<LeadshineMotionCard> _log;
    private readonly object _sync = new();

    private readonly double[] _lastPosition = new double[4];
    private readonly bool[] _estop = new bool[4];

    private bool _initialized;
    private bool _communicationError;
    private long _lastStopTick;
    private bool _disposed;

    /// <summary>构造真实运控卡（不在此初始化，需调用 <see cref="Initialize"/>）。</summary>
    /// <param name="options">整卡参数（卡号 + 4 轴）。</param>
    /// <param name="dllLock">与 <see cref="Io.MotionCardIoService"/> 共用的 DLL 串行化锁。</param>
    /// <param name="log">日志。</param>
    public LeadshineMotionCard(LeadshineMotionCardOptions options, SemaphoreSlim dllLock, ILogger<LeadshineMotionCard> log)
    {
        if (options.Axes is null || options.Axes.Length != 4)
        {
            throw new ArgumentException("雷赛运控卡需要 4 个轴的参数", nameof(options));
        }

        _cardNo = options.CardNo;
        _axes = options.Axes;
        _dllLock = dllLock;
        _log = log;
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

            // 幂等：已初始化成功时直接返回 true（注册工厂探测与 InitializeHardware 自检会各调一次）
            if (_initialized)
            {
                return true;
            }

            _dllLock.Wait();
            try
            {
                // 无卡 / 驱动缺失 / 位数不匹配时，DllImport 调用会抛异常而不是返回错误码：
                // 全部捕获并返回 false，让上层降级 Virtual + AL105（02 文档 4.3、11 文档 S8 验收 1）。
                ushort ret;
                try
                {
                    ret = LeadshineApi.dmc_board_init();
                }
                catch (DllNotFoundException ex)
                {
                    _log.LogError(ex, "未找到 LTDMC.dll，运控卡不可用");
                    return false;
                }
                catch (BadImageFormatException ex)
                {
                    _log.LogError(ex, "LTDMC.dll 位数与本进程不匹配，运控卡不可用");
                    return false;
                }
                catch (EntryPointNotFoundException ex)
                {
                    _log.LogError(ex, "LTDMC.dll 函数签名不匹配，运控卡不可用");
                    return false;
                }

                if (ret != 0)
                {
                    _log.LogError("运控卡初始化失败，错误码 {Code}", ret);
                    return false;
                }

                for (var i = 0; i < 4; i++)
                {
                    if (!ConfigureAxisLocked((AxisId)i))
                    {
                        TryCloseBoardLocked();
                        return false;
                    }
                }

                _initialized = true;
                _communicationError = false;
                _log.LogInformation("雷赛运控卡初始化成功（卡号 {Card}）", _cardNo);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "运控卡初始化异常");
                return false;
            }
            finally
            {
                _dllLock.Release();
            }
        }
    }

    /// <inheritdoc />
    public bool IsConnected
    {
        get { lock (_sync) { return _initialized && !_communicationError && !_disposed; } }
    }

    /// <inheritdoc />
    public double GetPosition(AxisId axis)
    {
        _dllLock.Wait();
        try
        {
            if (!EnsureOperational(axis))
            {
                return _lastPosition[(int)axis];
            }

            var ret = LeadshineApi.dmc_get_position_unit(_cardNo, (ushort)axis, out var pos);
            if (ret != 0)
            {
                MarkCommunicationError("读取位置", ret);
                return _lastPosition[(int)axis];
            }

            _lastPosition[(int)axis] = pos;
            return pos;
        }
        catch (Exception ex)
        {
            MarkCommunicationError("读取位置", ex);
            return _lastPosition[(int)axis];
        }
        finally
        {
            _dllLock.Release();
        }
    }

    /// <inheritdoc />
    public bool IsInPosition(AxisId axis)
    {
        _dllLock.Wait();
        try
        {
            if (!EnsureOperational(axis))
            {
                return false;
            }

            var done = LeadshineApi.dmc_check_done(_cardNo, (ushort)axis);
            if (done == 0)
            {
                return false;
            }

            // 到位但可能因限位/驱动报警而停：此时不能算「到位成功」（与 Virtual 语义一致）。
            return GetStopReasonLocked(axis) == MotionErrorCodes.Normal;
        }
        catch (Exception ex)
        {
            MarkCommunicationError("查询到位", ex);
            return false;
        }
        finally
        {
            _dllLock.Release();
        }
    }

    /// <inheritdoc />
    public int GetStopReason(AxisId axis)
    {
        _dllLock.Wait();
        try
        {
            return GetStopReasonLocked(axis);
        }
        catch (Exception ex)
        {
            MarkCommunicationError("读取停止原因", ex);
            return MotionErrorCodes.Normal;
        }
        finally
        {
            _dllLock.Release();
        }
    }

    /// <inheritdoc />
    public void Home(AxisId axis)
    {
        _dllLock.Wait();
        try
        {
            if (!EnsureMovable(axis))
            {
                return;
            }

            var cfg = _axes[(int)axis];
            Check(LeadshineApi.dmc_set_home_profile_unit(_cardNo, (ushort)axis, cfg.HomeSpeed, cfg.HomeAcc, cfg.HomeMode),
                  "设置回零参数", axis);
            Check(LeadshineApi.dmc_home_move(_cardNo, (ushort)axis), "回零", axis);
            _log.LogDebug("轴 {Axis} 开始回零", axis);
        }
        catch (Exception ex)
        {
            HandleMotionException("回零", axis, ex);
        }
        finally
        {
            _dllLock.Release();
        }
    }

    /// <inheritdoc />
    public bool IsHomeCompleted(AxisId axis)
    {
        _dllLock.Wait();
        try
        {
            if (!EnsureOperational(axis))
            {
                return false;
            }

            var ret = LeadshineApi.dmc_get_home_result(_cardNo, (ushort)axis, out var stopFlag);
            if (ret != 0)
            {
                MarkCommunicationError("查询回零结果", ret);
                return false;
            }

            // stopFlag：0=回零中，非 0=已结束（现场以手册为准，见 02 文档 3.1 型号适配）。
            return stopFlag != 0;
        }
        catch (Exception ex)
        {
            MarkCommunicationError("查询回零结果", ex);
            return false;
        }
        finally
        {
            _dllLock.Release();
        }
    }

    /// <inheritdoc />
    public void MoveAbsolute(AxisId axis, double position, double speed) => MoveTo(axis, position, speed, absolute: true);

    /// <inheritdoc />
    public void MoveRelative(AxisId axis, double distance, double speed) => MoveTo(axis, distance, speed, absolute: false);

    /// <inheritdoc />
    public void JogStart(AxisId axis, int direction, double speed)
    {
        _dllLock.Wait();
        try
        {
            if (!EnsureMovable(axis))
            {
                return;
            }

            SetSpeedLocked(axis, speed);
            Check(LeadshineApi.dmc_vmove(_cardNo, (ushort)axis, (ushort)(direction >= 0 ? 1 : 0)), "点动", axis);
        }
        catch (Exception ex)
        {
            HandleMotionException("点动", axis, ex);
        }
        finally
        {
            _dllLock.Release();
        }
    }

    /// <inheritdoc />
    public void Stop(AxisId axis, bool emergency)
    {
        _dllLock.Wait();
        try
        {
            Check(LeadshineApi.dmc_stop(_cardNo, (ushort)axis, (ushort)(emergency ? 1 : 0)),
                  emergency ? "急停" : "减速停", axis);

            if (emergency)
            {
                _estop[(int)axis] = true;
            }

            _lastStopTick = Environment.TickCount64;
            _log.LogDebug("轴 {Axis} 停止（{Mode}）", axis, emergency ? "急停" : "减速停");
        }
        catch (Exception ex)
        {
            HandleMotionException("停止", axis, ex);
        }
        finally
        {
            _dllLock.Release();
        }
    }

    /// <inheritdoc />
    public void EmergencyStopAll()
    {
        _dllLock.Wait();
        try
        {
            Check(LeadshineApi.dmc_emg_stop(_cardNo), "全轴急停");
            for (var i = 0; i < 4; i++)
            {
                _estop[i] = true;
            }

            _lastStopTick = Environment.TickCount64;
            _log.LogWarning("全轴急停");
        }
        catch (Exception ex)
        {
            MarkCommunicationError("全轴急停", ex);
        }
        finally
        {
            _dllLock.Release();
        }
    }

    /// <inheritdoc />
    public void ResetAxis(AxisId axis)
    {
        lock (_sync)
        {
            _estop[(int)axis] = false;
            _log.LogInformation("轴 {Axis} 已复位", axis);
        }

        // 软件层急停锁已清除；若现场板卡急停后还需硬件级复位（部分型号需 dmc_board_reset），
        // 由现场在 LeadshineApi.cs 内补充调用，此处不扩散到业务层。
    }

    /// <inheritdoc />
    public void SetSoftLimit(AxisId axis, bool enable, double min, double max)
    {
        _dllLock.Wait();
        try
        {
            if (!EnsureOperational(axis))
            {
                return;
            }

            Check(LeadshineApi.dmc_set_softlimit_unit(_cardNo, (ushort)axis,
                      (ushort)(enable ? 1 : 0), 0, max, min), "设置软限位", axis);
        }
        catch (Exception ex)
        {
            HandleMotionException("设置软限位", axis, ex);
        }
        finally
        {
            _dllLock.Release();
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

            var reason = GetStopReason(axis);
            if (reason != MotionErrorCodes.Normal)
            {
                _log.LogError("轴 {Axis} 异常停止，原因 {Reason}", axis, reason);
                RaiseAxisFault(axis, reason);
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
        }

        _dllLock.Wait();
        try
        {
            if (_initialized)
            {
                LeadshineApi.dmc_board_close();
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "关闭运控卡失败");
        }
        finally
        {
            _dllLock.Release();
        }
    }

    // ==================== 内部实现 ====================

    /// <summary>绝对/相对定位核心：调速后下发运动指令（02 文档 4 节 MoveAbsolute）。</summary>
    private void MoveTo(AxisId axis, double positionOrDistance, double speed, bool absolute)
    {
        _dllLock.Wait();
        try
        {
            if (!EnsureMovable(axis))
            {
                return;
            }

            SetSpeedLocked(axis, speed);
            Check(LeadshineApi.dmc_pmove_unit(_cardNo, (ushort)axis, positionOrDistance, (ushort)(absolute ? 1 : 0)),
                  absolute ? "绝对定位" : "相对移动", axis);
            _log.LogDebug("轴 {Axis} {Mode} 目标={Target:F2} 速度={Speed:F1}",
                          axis, absolute ? "绝对定位" : "相对移动", positionOrDistance, speed);
        }
        catch (Exception ex)
        {
            HandleMotionException("定位", axis, ex);
        }
        finally
        {
            _dllLock.Release();
        }
    }

    /// <summary>在锁内配置单个轴：脉冲当量/速度曲线/限位逻辑/软限位。</summary>
    private bool ConfigureAxisLocked(AxisId axis)
    {
        var cfg = _axes[(int)axis];
        var ax = (ushort)axis;

        if (Check(LeadshineApi.dmc_set_equiv(_cardNo, ax, cfg.PulsePerUnit), "设置脉冲当量", axis))
        {
            return false;
        }

        if (Check(LeadshineApi.dmc_set_profile_unit(_cardNo, ax, cfg.MinSpeed, cfg.MaxSpeed, cfg.AccTimeMs, cfg.DecTimeMs),
                  "设置速度曲线", axis))
        {
            return false;
        }

        if (Check(LeadshineApi.dmc_set_softlimit_unit(_cardNo, ax, 1, 0, cfg.MaxLimit, cfg.MinLimit), "设置软限位", axis))
        {
            return false;
        }

        return true;
    }

    /// <summary>动态调速：重设速度曲线最大速度（02 文档 4 节 MoveAbsolute 注释）。</summary>
    private void SetSpeedLocked(AxisId axis, double speed)
    {
        var cfg = _axes[(int)axis];
        Check(LeadshineApi.dmc_set_profile_unit(_cardNo, (ushort)axis, cfg.MinSpeed, speed, cfg.AccTimeMs, cfg.DecTimeMs),
              "设置速度", axis);
    }

    /// <summary>锁内读取停止原因，并直接映射到 0~4（02 文档 4.2 节）。</summary>
    private int GetStopReasonLocked(AxisId axis)
    {
        if (!EnsureOperational(axis))
        {
            return MotionErrorCodes.Normal;
        }

        var ret = LeadshineApi.dmc_get_stop_reason(_cardNo, (ushort)axis, out var reason);
        if (ret != 0)
        {
            MarkCommunicationError("读取停止原因", ret);
            return MotionErrorCodes.Normal;
        }

        return reason;
    }

    /// <summary>运动前校验：未初始化/通讯中断/急停未复位/Stop 锁定窗口内 → 拒绝。</summary>
    private bool EnsureMovable(AxisId axis)
    {
        if (!EnsureOperational(axis))
        {
            return false;
        }

        if (_estop[(int)axis])
        {
            _log.LogWarning("轴 {Axis} 处于急停状态，拒绝运动（需先复位）", axis);
            throw new HardwareException(DeviceName, MotionErrorCodes.EmergencyStop, $"轴 {axis} 处于急停状态，请先复位");
        }

        if (Environment.TickCount64 - _lastStopTick < StopLockoutMs)
        {
            _log.LogWarning("轴 {Axis} 停止后锁定期内，拒绝新运动", axis);
            throw new HardwareException(DeviceName, 0, "停止后 500ms 内禁止下发新运动");
        }

        return true;
    }

    /// <summary>板卡可用性校验：未初始化/已释放/通讯中断时返回 false。</summary>
    private bool EnsureOperational(AxisId axis)
    {
        lock (_sync)
        {
            if (!_initialized || _communicationError || _disposed)
            {
                _log.LogWarning("轴 {Axis} 运动请求被拒绝：运控卡未就绪", axis);
                return false;
            }
        }

        return true;
    }

    /// <summary>校验 DLL 返回码：非 0 抛 <see cref="HardwareException"/>，0 返回 false。</summary>
    private bool Check(ushort ret, string operation, AxisId axis)
    {
        if (ret == 0)
        {
            return false;
        }

        throw new HardwareException(DeviceName, ret, $"运控卡 {operation} 失败，轴={axis}，错误码={ret}");
    }

    /// <summary>校验 DLL 返回码（无轴上下文）。</summary>
    private void Check(ushort ret, string operation)
    {
        if (ret != 0)
        {
            throw new HardwareException(DeviceName, ret, $"运控卡 {operation} 失败，错误码={ret}");
        }
    }

    /// <summary>记录一次通讯错误（仅首次日志），并使 IsConnected 返回 false，由协调器报 AL105。</summary>
    private void MarkCommunicationError(string operation, int code)
    {
        lock (_sync)
        {
            if (_communicationError)
            {
                return;
            }

            _communicationError = true;
        }

        _log.LogError("运控卡 {Operation} 通讯失败，错误码 {Code}", operation, code);
    }

    private void MarkCommunicationError(string operation, Exception ex)
    {
        lock (_sync)
        {
            if (_communicationError)
            {
                return;
            }

            _communicationError = true;
        }

        _log.LogError(ex, "运控卡 {Operation} 通讯异常", operation);
    }

    /// <summary>运动类操作异常：通讯类异常标记掉线，其余包装后抛出（由上层流程捕获）。</summary>
    private void HandleMotionException(string operation, AxisId axis, Exception ex)
    {
        if (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            MarkCommunicationError(operation, ex);
            return;
        }

        if (ex is HardwareException)
        {
            _log.LogError(ex, "运控卡 {Operation} 失败（轴 {Axis}）", operation, axis);
            // 保留原始调用栈重新抛出（本方法由各 catch 块调用，不能直接使用裸 throw）
            ExceptionDispatchInfo.Capture(ex).Throw();
        }

        MarkCommunicationError(operation, ex);
    }

    /// <summary>触发轴异常事件（供上层演示/扩展订阅）。</summary>
    private void RaiseAxisFault(AxisId axis, int reason)
    {
        var message = MotionErrorCodes.GetDescription(reason);
        _log.LogWarning("轴 {Axis} 触发异常：{Message}", axis, message);
        AxisFault?.Invoke(this, new AxisFaultEventArgs { Axis = axis, Reason = reason, Message = message });
    }

    /// <summary>初始化失败时尝试关闭已打开的板卡。</summary>
    private void TryCloseBoardLocked()
    {
        try
        {
            LeadshineApi.dmc_board_close();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "关闭运控卡失败");
        }
    }
}
