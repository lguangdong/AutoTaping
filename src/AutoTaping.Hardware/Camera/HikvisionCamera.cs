// 职责：海康工业相机实现——MVS SDK 软触发取图、曝光/增益控制、掉线检测与后台重连
// 层：Hardware/Camera
// 依赖：MvCameraControl.Net.dll（原生 SDK，仅定义 HAS_MVS 编译常量时编译真实实现）、ICamera（Core/Abstractions）、ILogger

using AutoTaping.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Hardware.Camera;

#if HAS_MVS
// 说明：本实现引用海康 MVS SDK 的 .NET 封装（MvCameraControl.Net）。由于 MVS SDK 各版本命名空间
// 与类名有差异（Mvs / MvCameraControl；MvCamera / MvsCamera），以下类名与常量以 03 文档第 2 节为准，
// 现场接入时若与所装 SDK 版本不符，仅在本文件内调整（03 文档第 8 节：SDK 调用只能出现在本文件）。

using System.Runtime.InteropServices;
using Mvs;

/// <summary>
/// <see cref="ICamera"/> 的真实实现，基于海康 MVS SDK（03 文档第 2 节）。
/// 关键约束：取图不得在 UI 线程调用、不可并发取图（内部 <see cref="SemaphoreSlim"/> 串行化）、
/// 自动曝光/自动增益强制关闭、掉线后 3s 后台重连（03 文档第 6 节）。
/// </summary>
public sealed class HikvisionCamera : ICamera
{
    /// <summary>掉线后重连间隔（ms）。03 文档第 6 节：后台每 3s 尝试重新初始化。</summary>
    private const int ReconnectIntervalMs = 3_000;

    private readonly HikvisionCameraOptions _options;
    private readonly ILogger<HikvisionCamera> _log;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _grabLock = new(1, 1);

    private IntPtr _handle = IntPtr.Zero;
    private int _payloadSize;
    private bool _initialized;
    private bool _isGrabbing;
    private bool _disconnected;
    private bool _disposed;
    private CancellationTokenSource? _reconnectCts;

    /// <summary>构造海康相机（不在此初始化，需调用 <see cref="Initialize"/>）。</summary>
    /// <param name="options">相机参数。</param>
    /// <param name="log">日志。</param>
    public HikvisionCamera(HikvisionCameraOptions options, ILogger<HikvisionCamera> log)
    {
        _options = options;
        _log = log;
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

            // 幂等：已打开成功时直接返回 true（注册工厂探测与 InitializeHardware 自检会各调一次）
            if (_initialized && !_disconnected)
            {
                return true;
            }

            try
            {
                return OpenLocked();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "相机初始化失败");
                return false;
            }
        }
    }

    /// <inheritdoc />
    public bool IsConnected
    {
        get { lock (_sync) { return _initialized && !_disconnected && !_disposed; } }
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
            if (!IsConnected)
            {
                return false;
            }

            try
            {
                var ret = MvCamera.MV_CC_StartGrabbing(_handle);
                if (ret != MvCamera.MV_OK)
                {
                    _log.LogError("相机开始采集失败 0x{Code:X}", ret);
                    return false;
                }

                _isGrabbing = true;
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "相机开始采集异常");
                return false;
            }
        }
    }

    /// <inheritdoc />
    public void StopGrabbing()
    {
        lock (_sync)
        {
            if (!_isGrabbing)
            {
                return;
            }

            try
            {
                MvCamera.MV_CC_StopGrabbing(_handle);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "相机停止采集异常");
            }

            _isGrabbing = false;
        }
    }

    /// <inheritdoc />
    public async Task<CaptureResult> TriggerAndGrabAsync(int timeoutMs, CancellationToken ct)
    {
        await _grabLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await Task.Run(() => GrabOneFrame(timeoutMs), ct).ConfigureAwait(false);
            if (result.Success)
            {
                return result;
            }

            HandleGrabFailure(result.ErrorMessage ?? "取图失败");
            return result;
        }
        finally
        {
            _grabLock.Release();
        }
    }

    /// <inheritdoc />
    public void SoftwareTrigger()
    {
        lock (_sync)
        {
            if (!IsConnected)
            {
                return;
            }

            try
            {
                MvCamera.MV_CC_SetCommandValue(_handle, "TriggerSoftware");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "软触发失败");
            }
        }
    }

    /// <inheritdoc />
    public bool SetExposure(double microseconds)
    {
        lock (_sync)
        {
            if (!IsConnected)
            {
                return false;
            }

            try
            {
                return MvCamera.MV_CC_SetFloatValue(_handle, "ExposureTime", (float)microseconds) == MvCamera.MV_OK;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "设置曝光失败");
                return false;
            }
        }
    }

    /// <inheritdoc />
    public bool SetGain(double gain)
    {
        lock (_sync)
        {
            if (!IsConnected)
            {
                return false;
            }

            try
            {
                return MvCamera.MV_CC_SetFloatValue(_handle, "Gain", (float)gain) == MvCamera.MV_OK;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "设置增益失败");
                return false;
            }
        }
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

        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();

        lock (_sync)
        {
            try
            {
                if (_handle != IntPtr.Zero)
                {
                    if (_isGrabbing)
                    {
                        MvCamera.MV_CC_StopGrabbing(_handle);
                    }

                    MvCamera.MV_CC_CloseDevice(_handle);
                    MvCamera.MV_CC_DestroyHandle(_handle);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "释放相机失败");
            }
            finally
            {
                _handle = IntPtr.Zero;
                _initialized = false;
                _isGrabbing = false;
            }
        }

        _grabLock.Dispose();
    }

    // ==================== 内部实现 ====================

    /// <summary>枚举 + 打开 + 配置（自动曝光/增益强制关闭，曝光/增益来自配置）。持锁调用。</summary>
    private bool OpenLocked()
    {
        var deviceList = new MvCamera.MV_CC_DEVICE_INFO_LIST();
        var ret = MvCamera.MV_CC_EnumDevices(MvCamera.MV_GIGE_DEVICE | MvCamera.MV_USB_DEVICE, ref deviceList);
        if (ret != MvCamera.MV_OK || deviceList.nDeviceNum == 0)
        {
            _log.LogWarning("未枚举到工业相机（0x{Code:X}，设备数 {Count}）", ret, deviceList.nDeviceNum);
            return false;
        }

        // 单相机取第 0 台；配置了序列号则按序列号匹配（多相机场景）
        var info = deviceList.pDeviceInfo[0];
        if (!string.IsNullOrWhiteSpace(_options.SerialNumber))
        {
            // 序列号匹配逻辑依赖 SDK 版本，此处按文档留占位；单相机场景通常无需匹配
        }

        ret = MvCamera.MV_CC_CreateHandle(ref info, out var handle);
        if (ret != MvCamera.MV_OK || handle == IntPtr.Zero)
        {
            _log.LogError("创建相机句柄失败 0x{Code:X}", ret);
            return false;
        }

        _handle = handle;

        ret = MvCamera.MV_CC_OpenDevice(handle, MvCamera.MV_ACCESS_Exclusive, 0);
        if (ret != MvCamera.MV_OK)
        {
            _log.LogError("打开相机失败 0x{Code:X}", ret);
            MvCamera.MV_CC_DestroyHandle(handle);
            _handle = IntPtr.Zero;
            return false;
        }

        // GigE 必须设最优包长，否则可能丢帧（03 文档 2.2 第 4 步）
        if (info.nTLayerType == MvCamera.MV_GIGE_DEVICE)
        {
            MvCamera.MV_GIGE_SetOptimalPacketSize(handle);
        }

        // 触发模式：软触发（03 文档 2.2 第 5 步）
        MvCamera.MV_CC_SetEnumValue(handle, "TriggerMode", 1);
        MvCamera.MV_CC_SetEnumValue(handle, "TriggerSource", 7);   // 7 = Software（以枚举名为准）
        MvCamera.MV_CC_SetEnumValue(handle, "TriggerActivation", 0);

        // 自动曝光/自动增益强制关闭（03 文档第 8 节：代码写死，不用配置）
        MvCamera.MV_CC_SetEnumValue(handle, "ExposureAuto", 0);
        MvCamera.MV_CC_SetEnumValue(handle, "GainAuto", 0);
        MvCamera.MV_CC_SetFloatValue(handle, "ExposureTime", (float)_options.ExposureTimeUs);
        MvCamera.MV_CC_SetFloatValue(handle, "Gain", (float)_options.GainDb);

        _initialized = true;
        _disconnected = false;
        _log.LogInformation("海康相机打开成功（曝光 {Exposure}μs，增益 {Gain}dB）", _options.ExposureTimeUs, _options.GainDb);
        return true;
    }

    /// <summary>同步软触发 + 取一帧（后台线程调用）。</summary>
    private CaptureResult GrabOneFrame(int timeoutMs)
    {
        IntPtr handle;
        lock (_sync)
        {
            if (!IsConnected || !_isGrabbing)
            {
                return new CaptureResult { Success = false, ErrorMessage = "相机未就绪" };
            }

            handle = _handle;
        }

        try
        {
            MvCamera.MV_CC_SetCommandValue(handle, "TriggerSoftware");

            var buffer = new byte[_payloadSize];
            var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var frameInfo = new MvCamera.MV_FRAME_OUT_INFO_EX();
                var ret = MvCamera.MV_CC_GetOneFrameTimeout(
                    handle, pinned.AddrOfPinnedObject(), (uint)buffer.Length, ref frameInfo, (uint)timeoutMs);

                if (ret != MvCamera.MV_OK)
                {
                    return new CaptureResult { Success = false, ErrorMessage = $"取图失败 0x{ret:X}" };
                }

                return new CaptureResult
                {
                    Success = true,
                    Mono8Data = buffer,
                    Width = (int)frameInfo.nWidth,
                    Height = (int)frameInfo.nHeight,
                    Timestamp = DateTime.Now,
                };
            }
            finally
            {
                pinned.Free();
            }
        }
        catch (Exception ex)
        {
            return new CaptureResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>取图失败：标记掉线、触发 <see cref="Fault"/>（仅首次）、启动后台重连。</summary>
    private void HandleGrabFailure(string message)
    {
        bool firstFault;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            firstFault = !_disconnected;
            _disconnected = true;
            _isGrabbing = false;
        }

        _log.LogWarning("相机取图失败：{Message}", message);

        if (firstFault)
        {
            Fault?.Invoke(this, new CameraFaultEventArgs { Message = "相机通讯中断", ErrorCode = -1 });
        }

        StartReconnectLoop();
    }

    /// <summary>启动后台重连循环（重复调用安全）。</summary>
    private void StartReconnectLoop()
    {
        lock (_sync)
        {
            if (_reconnectCts is not null)
            {
                return;
            }

            _reconnectCts = new CancellationTokenSource();
        }

        var ct = _reconnectCts.Token;
        _ = Task.Run(() => ReconnectLoopAsync(ct), ct);
    }

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(ReconnectIntervalMs, ct);

            try
            {
                if (TryReopen())
                {
                    _log.LogInformation("相机已恢复连接");
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "相机重连失败");
            }
        }
    }

    /// <summary>尝试重新打开相机（03 文档第 6 节：重连成功清报警）。</summary>
    private bool TryReopen()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }

            // 先关闭旧句柄，再重新打开
            CloseHandleLocked();
            try
            {
                if (OpenLocked())
                {
                    _disconnected = false;
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "相机重连打开失败");
            }

            return false;
        }
    }

    /// <summary>关闭当前句柄（持锁调用）。</summary>
    private void CloseHandleLocked()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            MvCamera.MV_CC_CloseDevice(_handle);
            MvCamera.MV_CC_DestroyHandle(_handle);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "关闭相机句柄失败");
        }
        finally
        {
            _handle = IntPtr.Zero;
            _initialized = false;
            _isGrabbing = false;
        }
    }
}

#else

/// <summary>
/// <see cref="ICamera"/> 的占位实现（未引用海康 MVS SDK 时使用）。
/// <see cref="Initialize"/> 恒返回 <see langword="false"/>，由启动自检降级为 Virtual 并报 AL201。
/// 接入真实相机时：将 <c>MvCameraControl.Net.dll</c> 放入 <c>src/AutoTaping.Hardware/libs/</c> 并重新编译，
/// 项目会自动定义 <c>HAS_MVS</c> 并切换到上方真实实现（03 文档第 2 节）。
/// </summary>
public sealed class HikvisionCamera : ICamera
{
    private readonly ILogger<HikvisionCamera> _log;

    /// <summary>构造占位相机。</summary>
    public HikvisionCamera(HikvisionCameraOptions options, ILogger<HikvisionCamera> log)
    {
        _log = log;
    }

    // 占位实现永不触发掉线，故 Fault 事件不会被引用；接口要求保留该成员（CS0067 为预期警告，显式忽略）
#pragma warning disable CS0067
    /// <inheritdoc />
    public event EventHandler<CameraFaultEventArgs>? Fault;
#pragma warning restore CS0067

    /// <inheritdoc />
    public bool Initialize()
    {
        _log.LogWarning("未引用海康 MVS SDK，真实相机不可用，降级为 Virtual 相机");
        return false;
    }

    /// <inheritdoc />
    public bool IsConnected => false;

    /// <inheritdoc />
    public bool IsGrabbing => false;

    /// <inheritdoc />
    public bool StartGrabbing() => false;

    /// <inheritdoc />
    public void StopGrabbing()
    {
    }

    /// <inheritdoc />
    public Task<CaptureResult> TriggerAndGrabAsync(int timeoutMs, CancellationToken ct)
        => Task.FromResult(new CaptureResult { Success = false, ErrorMessage = "相机未连接" });

    /// <inheritdoc />
    public void SoftwareTrigger()
    {
    }

    /// <inheritdoc />
    public bool SetExposure(double microseconds) => false;

    /// <inheritdoc />
    public bool SetGain(double gain) => false;

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

#endif
