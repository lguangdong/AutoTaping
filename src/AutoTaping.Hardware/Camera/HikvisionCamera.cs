// 职责：海康工业相机实现——MVS SDK 软触发取图、曝光/增益控制、掉线检测与后台重连
// 层：Hardware/Camera
// 依赖：MvCameraControl.Net.dll（原生 SDK，仅定义 HAS_MVS 编译常量时编译真实实现）、ICamera（Core/Abstractions）、ILogger

using AutoTaping.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Hardware.Camera;

#if HAS_MVS

// 说明：本实现基于海康 MVS 4.x 的 .NET 封装（程序集 MvCameraControl.Net.dll，命名空间 MvCamCtrl.NET，
// 设备类 MyCamera，方法统一带 _NET 后缀）。MVS 3.x 的封装类名/方法名不同，如需兼容只能在本文
// 件内调整（03 文档第 8 节：SDK 调用只能出现在本文件）。
// 机型：MV-CA060-10GM（6MP / 3072×2048 / Mono8 / GigE），相机本体不支持 Auto 曝光以外的花活，
// 所有参数都走 GenICam 节点名访问，换同系列机型无需改代码。

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using MvCamCtrl.NET;

/// <summary>
/// <see cref="ICamera"/> 的真实实现，基于海康 MVS SDK（03 文档第 2 节）。
/// 关键约束：取图不得在 UI 线程调用、不可并发取图（内部 <see cref="SemaphoreSlim"/> 串行化）、
/// 自动曝光/自动增益强制关闭、掉线后 3s 后台重连（03 文档第 6 节）。
/// 线程模型：<see cref="SemaphoreSlim"/> 保护「对相机句柄的所有 SDK 调用」，
/// 普通 <c>lock</c> 只保护内存状态标志，因此 <see cref="IsConnected"/> 这类属性查询永远不会被 SDK 阻塞。
/// </summary>
public sealed class HikvisionCamera : ICamera
{
    /// <summary>掉线后重连间隔（ms）。03 文档第 6 节：后台每 3s 尝试重新初始化。</summary>
    private const int ReconnectIntervalMs = 3_000;

    /// <summary>判定掉线的「无有效图」阈值（ms）。03 文档第 6 节：超过 5s 没有成功取图才算通讯中断。</summary>
    private const int FaultThresholdMs = 5_000;

    /// <summary>访问相机句柄的等待上限（ms），超时直接返回失败，避免 UI 线程被 SDK 长时间挂住。</summary>
    private const int DeviceLockTimeoutMs = 5_000;

    /// <summary>GenICam 像素格式常量 PixelType_Gvsp_Mono8（0x01080001），用数值避免枚举类型随 SDK 版本变化。</summary>
    private const uint PixelTypeMono8 = 0x0108_0001;

    /// <summary>读取 PayloadSize 失败时的兜底缓冲区大小：MV-CA060-10GM 全幅 Mono8 = 3072×2048。</summary>
    private const int DefaultPayloadSize = 3072 * 2048;

    /// <summary>探测不到相机节点范围时使用的曝光上下限（μs），取 MV-CA060-10GM 规格 27μs~2.5s。</summary>
    private const double FallbackMinExposureUs = 27.0;

    /// <summary>探测不到相机节点范围时使用的曝光上限（μs）。</summary>
    private const double FallbackMaxExposureUs = 2_500_000.0;

    /// <summary>探测不到相机节点范围时使用的增益上下限（dB），取 MV-CA060-10GM 规格 0~20dB。</summary>
    private const double FallbackMinGainDb = 0.0;

    /// <summary>探测不到相机节点范围时使用的增益上限（dB）。</summary>
    private const double FallbackMaxGainDb = 20.0;

    private readonly HikvisionCameraOptions _options;
    private readonly ILogger<HikvisionCamera> _log;

    /// <summary>内存状态锁：只保护字段，绝不在持锁期间调用 SDK。</summary>
    private readonly object _stateLock = new();

    /// <summary>SDK 调用锁：打开/关闭/取图/设参全部串行，保证同一时刻只有一个线程碰句柄。</summary>
    private readonly SemaphoreSlim _deviceLock = new(1, 1);

    private MyCamera? _camera;
    private int _payloadSize;
    private double _exposureUs;
    private double _gainDb;
    private DateTime _lastSuccessUtc = DateTime.UtcNow;

    /// <summary>是否软触发（TriggerSource=Software）。硬触发时取图前不再发 TriggerSoftware 命令。</summary>
    private bool _softwareTrigger = true;

    private bool _initialized;
    private bool _isGrabbing;
    private bool _disconnected;
    private bool _disposed;

    /// <summary>SDK 句柄是否已创建（未创建时不能调用 Close/Destroy，否则 SDK 会返回错误码刷日志）。</summary>
    private bool _deviceCreated;
    private CancellationTokenSource? _reconnectCts;

    /// <summary>构造海康相机（不在此初始化，需调用 <see cref="Initialize"/>）。</summary>
    /// <param name="options">相机参数。</param>
    /// <param name="log">日志。</param>
    public HikvisionCamera(HikvisionCameraOptions options, ILogger<HikvisionCamera> log)
    {
        _options = options;
        _log = log;
        _exposureUs = options.ExposureTimeUs;
        _gainDb = options.GainDb;
    }

    /// <inheritdoc />
    public event EventHandler<CameraFaultEventArgs>? Fault;

    /// <inheritdoc />
    /// <remarks>
    /// 找不到相机 / 未安装 MVS 原生库 / 位数不匹配时一律返回 <see langword="false"/> 而不抛异常，
    /// 由 DI 工厂降级为 Virtual 相机并报 AL201（03 文档第 7 节验收 1、2）。
    /// </remarks>
    public bool Initialize()
    {
        lock (_stateLock)
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
        }

        if (!TryAcquireDevice())
        {
            return false;
        }

        try
        {
            return OpenDeviceLocked();
        }
        catch (DllNotFoundException ex)
        {
            _log.LogError(ex, "未找到海康 MVS 原生库（MvCameraControl.dll），请安装 MVS 客户端并确认程序为 x64");
            return false;
        }
        catch (BadImageFormatException ex)
        {
            _log.LogError(ex, "海康 MVS 库位数与本程序不一致（需同为 x64），请检查 MVS 安装与平台目标");
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            _log.LogError(ex, "海康 MVS SDK 版本与代码不匹配，请使用 MVS 4.x 的 .NET 封装（MvCamCtrl.NET.MyCamera）");
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "相机初始化失败");
            return false;
        }
        finally
        {
            _deviceLock.Release();
        }
    }

    /// <inheritdoc />
    public bool IsConnected
    {
        get { lock (_stateLock) { return _initialized && !_disconnected && !_disposed; } }
    }

    /// <inheritdoc />
    public bool IsGrabbing
    {
        get { lock (_stateLock) { return _isGrabbing; } }
    }

    /// <inheritdoc />
    public bool StartGrabbing()
    {
        if (!TryAcquireDevice())
        {
            return false;
        }

        try
        {
            return StartGrabbingLocked();
        }
        finally
        {
            _deviceLock.Release();
        }
    }

    /// <inheritdoc />
    public void StopGrabbing()
    {
        if (!TryAcquireDevice())
        {
            return;
        }

        try
        {
            StopGrabbingLocked();
        }
        finally
        {
            _deviceLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CaptureResult> TriggerAndGrabAsync(int timeoutMs, CancellationToken ct)
    {
        var timeout = timeoutMs > 0 ? timeoutMs : _options.TimeoutMs;

        CaptureResult result;
        await _deviceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 同步阻塞的 SDK 取图放到线程池，不占用调用方（可能是 UI）线程
            result = await Task.Run(() => GrabCore(timeout), ct).ConfigureAwait(false);
        }
        finally
        {
            _deviceLock.Release();
        }

        if (!result.Success)
        {
            HandleGrabFailure(result.ErrorMessage ?? "取图失败");
        }

        return result;
    }

    /// <inheritdoc />
    public void SoftwareTrigger()
    {
        if (!TryAcquireDevice())
        {
            return;
        }

        try
        {
            MyCamera? camera;
            lock (_stateLock)
            {
                if (_disposed || !_initialized || _disconnected)
                {
                    return;
                }

                camera = _camera;
            }

            if (camera is null)
            {
                return;
            }

            var ret = camera.MV_CC_SetCommandValue_NET("TriggerSoftware");
            if (ret != MyCamera.MV_OK)
            {
                _log.LogWarning("软触发失败（0x{Code:X8}），请确认 TriggerMode=On、TriggerSource=Software", ret);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "软触发异常");
        }
        finally
        {
            _deviceLock.Release();
        }
    }

    /// <inheritdoc />
    public bool SetExposure(double microseconds)
    {
        if (!TryAcquireDevice())
        {
            return false;
        }

        try
        {
            MyCamera? camera;
            lock (_stateLock)
            {
                if (_disposed || !_initialized || _disconnected)
                {
                    return false;
                }

                camera = _camera;
            }

            if (camera is null)
            {
                return false;
            }

            try
            {
                return ApplyExposureLocked(camera, microseconds);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "设置曝光异常");
                return false;
            }
        }
        finally
        {
            _deviceLock.Release();
        }
    }

    /// <inheritdoc />
    public bool SetGain(double gain)
    {
        if (!TryAcquireDevice())
        {
            return false;
        }

        try
        {
            MyCamera? camera;
            lock (_stateLock)
            {
                if (_disposed || !_initialized || _disconnected)
                {
                    return false;
                }

                camera = _camera;
            }

            if (camera is null)
            {
                return false;
            }

            try
            {
                return ApplyGainLocked(camera, gain);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "设置增益异常");
                return false;
            }
        }
        finally
        {
            _deviceLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CancellationTokenSource? reconnectCts;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _initialized = false;
            _isGrabbing = false;
            reconnectCts = _reconnectCts;
            _reconnectCts = null;
        }

        reconnectCts?.Cancel();
        reconnectCts?.Dispose();

        // 等正在进行的取图结束（最多 2s），避免销毁句柄时 SDK 还在往用户缓冲区写数据
        var acquired = false;
        try
        {
            acquired = _deviceLock.Wait(2_000);
        }
        catch (ObjectDisposedException)
        {
            // 已释放，直接走关闭流程
        }

        try
        {
            CloseDeviceLocked();
        }
        finally
        {
            if (acquired)
            {
                _deviceLock.Release();
            }

            _deviceLock.Dispose();
        }
    }

    // ==================== 打开 / 关闭 ====================

    /// <summary>
    /// 枚举 → 选设备 → 创建句柄 → 打开 → 配置 → 读缓冲区大小。持 <see cref="_deviceLock"/> 调用。
    /// 任何一步失败都保证已分配的句柄被回收，返回 <see langword="false"/>。
    /// </summary>
    private bool OpenDeviceLocked()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return false;
            }
        }

        // 重连场景：先把上一次的句柄清干净
        CloseDeviceLocked();

        var deviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
        var ret = MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE, ref deviceList);
        if (ret != MyCamera.MV_OK)
        {
            _log.LogError("枚举相机失败（0x{Code:X8}），请确认 MVS 客户端能正常看到设备", ret);
            return false;
        }

        if (deviceList.nDeviceNum == 0 || deviceList.pDeviceInfo is null)
        {
            _log.LogWarning("未枚举到工业相机，请检查相机供电、网线、网卡 IP 网段（MVS 客户端能否看到设备）");
            return false;
        }

        var index = FindDeviceIndex(deviceList);
        if (index < 0)
        {
            return false;
        }

        var deviceInfo = Marshal.PtrToStructure<MyCamera.MV_CC_DEVICE_INFO>(deviceList.pDeviceInfo[index]);
        var isGigE = deviceInfo.nTLayerType == MyCamera.MV_GIGE_DEVICE;

        var camera = _camera ??= new MyCamera();
        ret = camera.MV_CC_CreateDevice_NET(ref deviceInfo);
        if (ret != MyCamera.MV_OK)
        {
            _log.LogError("创建相机句柄失败（0x{Code:X8}）", ret);
            return false;
        }

        lock (_stateLock)
        {
            _deviceCreated = true;
        }

        ret = camera.MV_CC_OpenDevice_NET(MyCamera.MV_ACCESS_Exclusive, 0);
        if (ret != MyCamera.MV_OK)
        {
            _log.LogError("打开相机失败（0x{Code:X8}），设备可能被 MVS 客户端或其它程序占用", ret);
            SafeDestroy(camera);
            return false;
        }

        // GigE 必须设最优包长，否则 6MP 大图容易丢帧（03 文档 2.2 第 4 步）
        if (isGigE)
        {
            ApplyOptimalPacketSize(camera);
        }

        if (!ConfigureLocked(camera))
        {
            CloseDeviceLocked();
            return false;
        }

        var payloadSize = ReadPayloadSizeLocked(camera);
        var width = ReadIntNode(camera, "Width");
        var height = ReadIntNode(camera, "Height");

        lock (_stateLock)
        {
            _payloadSize = payloadSize;
            _initialized = true;
            _disconnected = false;
            _lastSuccessUtc = DateTime.UtcNow;
        }

        _log.LogInformation(
            "海康相机已打开：{Model} 序列号 {Serial}，{Width}×{Height} Mono8，曝光 {Exposure}μs，增益 {Gain}dB，缓冲区 {Payload} 字节",
            ReadDeviceString(deviceInfo, "chModelName"), ReadDeviceString(deviceInfo, "chSerialNumber"),
            width, height, _exposureUs, _gainDb, payloadSize);

        return true;
    }

    /// <summary>关闭并销毁当前句柄。持 <see cref="_deviceLock"/> 调用，可重复调用。</summary>
    private void CloseDeviceLocked()
    {
        MyCamera? camera;
        bool wasGrabbing;
        bool created;

        lock (_stateLock)
        {
            camera = _camera;
            wasGrabbing = _isGrabbing;
            created = _deviceCreated;
            _initialized = false;
            _isGrabbing = false;
            _deviceCreated = false;
        }

        if (camera is null || !created)
        {
            return;
        }

        try
        {
            if (wasGrabbing)
            {
                camera.MV_CC_StopGrabbing_NET();
            }

            camera.MV_CC_CloseDevice_NET();
            camera.MV_CC_DestroyDevice_NET();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "关闭相机句柄失败");
        }
    }

    /// <summary>销毁句柄但不做 StopGrabbing（打开失败后的清理路径）。</summary>
    private void SafeDestroy(MyCamera camera)
    {
        lock (_stateLock)
        {
            _deviceCreated = false;
            _initialized = false;
            _isGrabbing = false;
        }

        try
        {
            camera.MV_CC_DestroyDevice_NET();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "销毁相机句柄失败");
        }
    }

    /// <summary>按 <c>Hardware:Camera:SerialNumber</c> 选设备；未配置则取第一台。返回 -1 表示配置了序列号但没找到。</summary>
    private int FindDeviceIndex(MyCamera.MV_CC_DEVICE_INFO_LIST deviceList)
    {
        var count = (int)deviceList.nDeviceNum;
        var wanted = _options.SerialNumber.Trim();
        var found = -1;

        for (var i = 0; i < count; i++)
        {
            var info = Marshal.PtrToStructure<MyCamera.MV_CC_DEVICE_INFO>(deviceList.pDeviceInfo[i]);
            var model = ReadDeviceString(info, "chModelName");
            var serial = ReadDeviceString(info, "chSerialNumber");
            var layer = info.nTLayerType == MyCamera.MV_GIGE_DEVICE ? "GigE" : "USB3";

            _log.LogInformation("枚举到相机 {Index}：{Model} 序列号 {Serial} 接口 {Layer}", i, model, serial, layer);

            if (wanted.Length > 0
                && found < 0
                && string.Equals(serial, wanted, StringComparison.OrdinalIgnoreCase))
            {
                found = i;
            }
        }

        if (wanted.Length == 0)
        {
            return 0;
        }

        if (found < 0)
        {
            _log.LogError("未找到序列号为 {Serial} 的相机，请核对 appsettings.json 的 Hardware:Camera:SerialNumber", wanted);
        }

        return found;
    }

    /// <summary>GigE 最优包长（网卡未开巨帧时会自动退化为 1500，只影响吞吐不影响功能）。</summary>
    private void ApplyOptimalPacketSize(MyCamera camera)
    {
        var packetSize = camera.MV_CC_GetOptimalPacketSize_NET();
        if (packetSize <= 0)
        {
            _log.LogWarning("获取 GigE 最优包长失败（0x{Code:X8}），沿用相机默认值", packetSize);
            return;
        }

        var ret = camera.MV_CC_SetIntValue_NET("GevSCPSPacketSize", (uint)packetSize);
        if (ret != MyCamera.MV_OK)
        {
            _log.LogWarning("设置 GevSCPSPacketSize={PacketSize} 失败（0x{Code:X8}）", packetSize, ret);
            return;
        }

        _log.LogInformation("GigE 最优包长已设为 {PacketSize} 字节（网卡建议开巨帧 9014）", packetSize);
    }

    /// <summary>
    /// 像素格式 + 触发模式 + 自动曝光/增益关闭 + 曝光/增益。持 <see cref="_deviceLock"/> 调用。
    /// 像素格式与触发模式失败视为致命（否则会变成自由采集，拍照与运动不同步）。
    /// </summary>
    private bool ConfigureLocked(MyCamera camera)
    {
        // 1. 像素格式 Mono8：黑白相机最省带宽，且下游视觉按「一像素一字节」处理（03 文档第 5 节）
        var ret = camera.MV_CC_SetEnumValueByString_NET("PixelFormat", "Mono8");
        if (ret != MyCamera.MV_OK)
        {
            _log.LogError("设置像素格式 Mono8 失败（0x{Code:X8}），请确认机型为黑白相机", ret);
            return false;
        }

        // 2. 触发：TriggerMode=On(1)，触发源来自配置（默认 Software；硬件触发填 Line0/Line1…）
        var triggerSource = string.IsNullOrWhiteSpace(_options.TriggerMode) ? "Software" : _options.TriggerMode.Trim();

        ret = camera.MV_CC_SetEnumValue_NET("TriggerMode", 1);
        if (ret != MyCamera.MV_OK)
        {
            _log.LogError("设置 TriggerMode=On 失败（0x{Code:X8}）", ret);
            return false;
        }

        ret = camera.MV_CC_SetEnumValueByString_NET("TriggerSource", triggerSource);
        if (ret != MyCamera.MV_OK)
        {
            _log.LogError("设置 TriggerSource={Source} 失败（0x{Code:X8}），请核对 appsettings.json 的 Hardware:Camera:TriggerMode",
                          triggerSource, ret);
            return false;
        }

        LogIfFailed(camera.MV_CC_SetEnumValue_NET("TriggerActivation", 0), "TriggerActivation");

        var softwareTrigger = string.Equals(triggerSource, "Software", StringComparison.OrdinalIgnoreCase);
        lock (_stateLock)
        {
            _softwareTrigger = softwareTrigger;
        }

        if (!softwareTrigger)
        {
            _log.LogInformation("相机使用硬件触发（TriggerSource={Source}），上位机不再逐个软触发", triggerSource);
        }

        // 3. 自动曝光/自动增益强制关闭（03 文档第 8 节：代码写死，不由配置决定）
        LogIfFailed(camera.MV_CC_SetEnumValue_NET("ExposureAuto", 0), "ExposureAuto");
        LogIfFailed(camera.MV_CC_SetEnumValue_NET("GainAuto", 0), "GainAuto");

        // 4. 触发模式下不需要限帧
        LogIfFailed(camera.MV_CC_SetBoolValue_NET("AcquisitionFrameRateEnable", false), "AcquisitionFrameRateEnable");

        // 5. 曝光/增益（按相机自报的节点范围夹紧，换机型不用改代码）
        ApplyExposureLocked(camera, _exposureUs);
        ApplyGainLocked(camera, _gainDb);
        return true;
    }

    /// <summary>读 PayloadSize（缓冲区大小）；读不到时按 Width×Height 估算。</summary>
    private int ReadPayloadSizeLocked(MyCamera camera)
    {
        var param = new MyCamera.MVCC_INTVALUE();
        if (camera.MV_CC_GetIntValue_NET("PayloadSize", ref param) == MyCamera.MV_OK && param.nCurValue > 0)
        {
            return (int)param.nCurValue;
        }

        var width = ReadIntNode(camera, "Width");
        var height = ReadIntNode(camera, "Height");
        if (width > 0 && height > 0)
        {
            _log.LogWarning("读取 PayloadSize 失败，按 {Width}×{Height} 估算缓冲区", width, height);
            return width * height;
        }

        _log.LogWarning("读取 PayloadSize 失败，使用默认缓冲区 {Size} 字节（MV-CA060-10GM 全幅 Mono8）", DefaultPayloadSize);
        return DefaultPayloadSize;
    }

    // ==================== 采集 ====================

    /// <summary>开始采集。持 <see cref="_deviceLock"/> 调用。</summary>
    private bool StartGrabbingLocked()
    {
        MyCamera? camera;
        lock (_stateLock)
        {
            if (_disposed || !_initialized || _disconnected)
            {
                return false;
            }

            if (_isGrabbing)
            {
                return true;
            }

            camera = _camera;
        }

        if (camera is null)
        {
            return false;
        }

        var ret = camera.MV_CC_StartGrabbing_NET();
        if (ret != MyCamera.MV_OK)
        {
            _log.LogError("相机开始采集失败（0x{Code:X8}）", ret);
            return false;
        }

        lock (_stateLock)
        {
            _isGrabbing = true;
            _lastSuccessUtc = DateTime.UtcNow;
        }

        _log.LogInformation("相机开始采集（软触发模式）");
        return true;
    }

    /// <summary>停止采集。持 <see cref="_deviceLock"/> 调用。</summary>
    private void StopGrabbingLocked()
    {
        MyCamera? camera;
        lock (_stateLock)
        {
            if (!_isGrabbing)
            {
                return;
            }

            camera = _camera;
            _isGrabbing = false;
        }

        if (camera is null)
        {
            return;
        }

        try
        {
            camera.MV_CC_StopGrabbing_NET();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "相机停止采集异常");
        }
    }

    /// <summary>同步软触发 + 取一帧（线程池调用，<see cref="_deviceLock"/> 已被外层持有）。</summary>
    private CaptureResult GrabCore(int timeoutMs)
    {
        MyCamera? camera;
        int payloadSize;
        bool softwareTrigger;

        lock (_stateLock)
        {
            if (_disposed)
            {
                return Fail("相机已释放");
            }

            if (!_initialized || _disconnected)
            {
                return Fail("相机未连接");
            }

            if (!_isGrabbing)
            {
                return Fail("未开始采集");
            }

            camera = _camera;
            payloadSize = _payloadSize;
            softwareTrigger = _softwareTrigger;
        }

        if (camera is null || payloadSize <= 0)
        {
            return Fail("相机未就绪");
        }

        try
        {
            if (softwareTrigger)
            {
                var triggerRet = camera.MV_CC_SetCommandValue_NET("TriggerSoftware");
                if (triggerRet != MyCamera.MV_OK)
                {
                    return Fail($"软触发失败（0x{triggerRet:X8}）");
                }
            }

            var buffer = new byte[payloadSize];
            var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var frameInfo = new MyCamera.MV_FRAME_OUT_INFO_EX();
                var ret = camera.MV_CC_GetOneFrameTimeout_NET(
                    pinned.AddrOfPinnedObject(), (uint)buffer.Length, ref frameInfo, timeoutMs);

                if (ret != MyCamera.MV_OK)
                {
                    return Fail($"取图失败（0x{ret:X8}）");
                }

                if ((uint)frameInfo.enPixelType != PixelTypeMono8)
                {
                    return Fail($"像素格式非 Mono8（0x{(uint)frameInfo.enPixelType:X8}），请检查相机节点 PixelFormat");
                }

                var width = (int)frameInfo.nWidth;
                var height = (int)frameInfo.nHeight;
                var frameLen = (int)frameInfo.nFrameLen;

                if (width <= 0 || height <= 0 || frameLen < width * height)
                {
                    return Fail("取图失败（图像尺寸或数据长度非法）");
                }

                lock (_stateLock)
                {
                    _lastSuccessUtc = DateTime.UtcNow;
                }

                return new CaptureResult
                {
                    Success = true,
                    Mono8Data = buffer,
                    Width = width,
                    Height = height,
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
            return Fail(ex.Message);
        }
    }

    // ==================== 参数 ====================

    /// <summary>设置曝光（μs），按相机自报范围夹紧。持 <see cref="_deviceLock"/> 调用。</summary>
    private bool ApplyExposureLocked(MyCamera camera, double microseconds)
    {
        var value = ClampToCameraRange(camera, "ExposureTime", microseconds, FallbackMinExposureUs, FallbackMaxExposureUs);

        var ret = camera.MV_CC_SetFloatValue_NET("ExposureTime", (float)value);
        if (ret != MyCamera.MV_OK)
        {
            _log.LogError("设置曝光失败（0x{Code:X8}）", ret);
            return false;
        }

        lock (_stateLock)
        {
            _exposureUs = value;
        }

        if (Math.Abs(value - microseconds) > 0.5)
        {
            _log.LogWarning("曝光 {Requested}μs 超出相机范围，已按 {Applied}μs 设置", microseconds, value);
        }
        else
        {
            _log.LogInformation("相机曝光设置为 {Exposure}μs", value);
        }

        return true;
    }

    /// <summary>设置增益（dB），按相机自报范围夹紧。持 <see cref="_deviceLock"/> 调用。</summary>
    private bool ApplyGainLocked(MyCamera camera, double gain)
    {
        var value = ClampToCameraRange(camera, "Gain", gain, FallbackMinGainDb, FallbackMaxGainDb);

        var ret = camera.MV_CC_SetFloatValue_NET("Gain", (float)value);
        if (ret != MyCamera.MV_OK)
        {
            _log.LogError("设置增益失败（0x{Code:X8}）", ret);
            return false;
        }

        lock (_stateLock)
        {
            _gainDb = value;
        }

        if (Math.Abs(value - gain) > 0.01)
        {
            _log.LogWarning("增益 {Requested}dB 超出相机范围，已按 {Applied}dB 设置", gain, value);
        }
        else
        {
            _log.LogInformation("相机增益设置为 {Gain}dB", value);
        }

        return true;
    }

    /// <summary>读节点上下限并把请求值夹紧；读不到范围时退回机型规格常量。</summary>
    private static double ClampToCameraRange(MyCamera camera, string node, double value, double fallbackMin, double fallbackMax)
    {
        var param = new MyCamera.MVCC_FLOATVALUE();
        if (camera.MV_CC_GetFloatValue_NET(node, ref param) == MyCamera.MV_OK && param.fMax > param.fMin)
        {
            return Math.Clamp(value, param.fMin, param.fMax);
        }

        return Math.Clamp(value, fallbackMin, fallbackMax);
    }

    /// <summary>读整型节点（Width / Height / PayloadSize）；失败返回 0。</summary>
    private static int ReadIntNode(MyCamera camera, string node)
    {
        var param = new MyCamera.MVCC_INTVALUE();
        return camera.MV_CC_GetIntValue_NET(node, ref param) == MyCamera.MV_OK ? (int)param.nCurValue : 0;
    }

    /// <summary>记录非致命节点的设置失败（不同机型支持的节点不同，不因它中断打开流程）。</summary>
    private void LogIfFailed(int ret, string node)
    {
        if (ret != MyCamera.MV_OK)
        {
            _log.LogWarning("相机节点 {Node} 设置失败（0x{Code:X8}），该机型可能不支持此节点", node, ret);
        }
    }

    // ==================== 掉线 / 重连 ====================

    /// <summary>
    /// 取图失败：单次失败可能只是没来料或曝光过长，累计「5s 内没有任何成功取图」才判定掉线并触发
    /// <see cref="Fault"/>（03 文档第 6 节），避免一次超时就把设备打成降级。
    /// </summary>
    private void HandleGrabFailure(string message)
    {
        var raiseFault = false;
        var startReconnect = false;

        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            if (!_disconnected
                && (DateTime.UtcNow - _lastSuccessUtc).TotalMilliseconds >= FaultThresholdMs)
            {
                _disconnected = true;
                _isGrabbing = false;
                raiseFault = true;
            }

            startReconnect = _disconnected;
        }

        _log.LogWarning("相机取图失败：{Message}", message);

        if (raiseFault)
        {
            _log.LogError("相机连续 {Seconds}s 无有效图像，判定通讯中断，开始后台重连", FaultThresholdMs / 1000);
            Fault?.Invoke(this, new CameraFaultEventArgs { Message = "相机通讯中断", ErrorCode = -1 });
        }

        if (startReconnect)
        {
            StartReconnectLoop();
        }
    }

    /// <summary>启动后台重连循环（重复调用安全；循环结束后会复位 CTS，便于下一次掉线再起）。</summary>
    private void StartReconnectLoop()
    {
        CancellationToken token;

        lock (_stateLock)
        {
            if (_disposed || _reconnectCts is not null)
            {
                return;
            }

            _reconnectCts = new CancellationTokenSource();
            token = _reconnectCts.Token;
        }

        _ = Task.Run(() => ReconnectLoopAsync(token), token);
    }

    /// <summary>每 3s 尝试重新打开相机并恢复采集，成功即退出循环。</summary>
    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(ReconnectIntervalMs, ct).ConfigureAwait(false);

                var acquired = false;
                try
                {
                    await _deviceLock.WaitAsync(ct).ConfigureAwait(false);
                    acquired = true;

                    if (TryReopenLocked())
                    {
                        return;
                    }
                }
                finally
                {
                    if (acquired)
                    {
                        _deviceLock.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出（Dispose 或恢复成功）
        }
        catch (ObjectDisposedException)
        {
            // Dispose 已释放信号量
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "相机重连循环异常终止");
        }
        finally
        {
            lock (_stateLock)
            {
                _reconnectCts?.Dispose();
                _reconnectCts = null;
            }
        }
    }

    /// <summary>尝试重新打开并恢复采集。持 <see cref="_deviceLock"/> 调用。</summary>
    private bool TryReopenLocked()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return false;
            }
        }

        if (!OpenDeviceLocked())
        {
            return false;
        }

        if (!StartGrabbingLocked())
        {
            _log.LogWarning("相机已重新打开，但恢复采集失败，继续重试");
            lock (_stateLock)
            {
                _disconnected = true;
                _isGrabbing = false;
            }

            return false;
        }

        _log.LogInformation("相机已自动重连并恢复采集，自动流程保持暂停，请人工确认后继续");
        return true;
    }

    // ==================== 工具 ====================

    /// <summary>尝试获取 SDK 调用权（超时/已释放返回 false，调用方直接按失败处理）。</summary>
    private bool TryAcquireDevice()
    {
        try
        {
            return _deviceLock.Wait(DeviceLockTimeoutMs);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 从设备信息里读字符串字段（序列号 / 型号）。
    /// MVS 4.x 起 <c>MV_CC_DEVICE_INFO.SpecialInfo</c> 里放的是各传输层的**原始字节块**
    /// （<c>stGigEInfo</c> / <c>stUsb3VInfo</c> 都是 <c>byte[]</c>），必须用 SDK 自带的
    /// <c>MyCamera.ByteToStruct</c> 转成 <c>*_DEVICE_INFO_EX</c> 才能取字段；
    /// 老版本封装则是联合体里直接嵌套结构体。两条路都试，读不到返回空串
    /// （只影响日志与多相机选择，不影响单相机使用）。
    /// </summary>
    private static string ReadDeviceString(MyCamera.MV_CC_DEVICE_INFO info, string fieldName)
    {
        // 1. 新布局：SpecialInfo → byte[] → *_DEVICE_INFO_EX
        var specific = DecodeDeviceSpecificInfo(info);
        if (specific is not null)
        {
            var decoded = ReadTextField(specific, fieldName);
            if (decoded.Length > 0)
            {
                return decoded;
            }
        }

        // 2. 老布局：联合体字段本身就是嵌套结构体
        foreach (var field in info.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.FieldType.IsPrimitive || field.FieldType == typeof(string)
                || field.FieldType.IsEnum || field.FieldType.IsArray)
            {
                continue;
            }

            object? nested;
            try
            {
                nested = field.GetValue(info);
            }
            catch (Exception)
            {
                continue;
            }

            if (nested is null)
            {
                continue;
            }

            var text = ReadTextField(nested, fieldName);
            if (text.Length > 0)
            {
                return text;
            }
        }

        return "";
    }

    /// <summary>按传输层把 <c>SpecialInfo</c> 的原始字节块还原成 GigE / USB3 设备信息结构。</summary>
    private static object? DecodeDeviceSpecificInfo(MyCamera.MV_CC_DEVICE_INFO info)
    {
        var union = info.GetType().GetField("SpecialInfo", BindingFlags.Public | BindingFlags.Instance)?.GetValue(info);
        if (union is null)
        {
            return null;
        }

        var (blobName, structName) = info.nTLayerType switch
        {
            MyCamera.MV_GIGE_DEVICE => ("stGigEInfo", "MV_GIGE_DEVICE_INFO_EX"),
            MyCamera.MV_USB_DEVICE => ("stUsb3VInfo", "MV_USB3_DEVICE_INFO_EX"),
            _ => ("", ""),
        };

        if (blobName.Length == 0)
        {
            return null;
        }

        if (union.GetType().GetField(blobName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(union) is not byte[] blob
            || blob.Length == 0)
        {
            return null;
        }

        var structType = typeof(MyCamera).GetNestedType(structName, BindingFlags.Public);
        if (structType is null)
        {
            return null;
        }

        try
        {
            return MyCamera.ByteToStruct(blob, structType);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>读结构体里的定长字符串字段（<c>chSerialNumber</c> / <c>chModelName</c>）。</summary>
    private static string ReadTextField(object container, string fieldName)
    {
        var field = container.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
        if (field is null)
        {
            return "";
        }

        object? value;
        try
        {
            value = field.GetValue(container);
        }
        catch (Exception)
        {
            return "";
        }

        var text = value switch
        {
            string s => s,
            byte[] bytes => Encoding.ASCII.GetString(bytes),
            _ => null,
        };

        return text is null ? "" : text.Trim('\0', ' ');
    }

    /// <summary>构造失败结果。</summary>
    private static CaptureResult Fail(string message) => new() { Success = false, ErrorMessage = message };
}

#else

/// <summary>
/// <see cref="ICamera"/> 的占位实现（未引用海康 MVS SDK 时使用）。
/// <see cref="Initialize"/> 恒返回 <see langword="false"/>，由启动自检降级为 Virtual 并报 AL201。
/// 接入真实相机（如 MV-CA060-10GM）时：安装 MVS 客户端（4.x），把
/// <c>MvCameraControl.Net.dll</c> 放到 <c>src/AutoTaping.Hardware/libs/</c>（或让工程自动探测环境变量
/// <c>MVCAM_COMMON_RUNENV</c> 指向的 MVS 安装目录）后重新编译，工程会定义 <c>HAS_MVS</c>
/// 并切换到上方真实实现（03 文档第 2 节）。
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
        _log.LogWarning("未引用海康 MVS SDK，真实相机不可用，降级为 Virtual 相机（MV-CA060-10GM 需 HAS_MVS 编译常量）");
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
