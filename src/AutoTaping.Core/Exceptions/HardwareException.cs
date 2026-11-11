// 职责：硬件调用失败异常——带设备名与错误码（10-编码约定 6.1，由 Hardware 层抛出）
// 层：Core/Exceptions
// 依赖：无

namespace AutoTaping.Core.Exceptions;

/// <summary>
/// 硬件调用失败异常。用于统一包装底层 SDK / 板卡返回的错误，避免向 UI 裸露原始错误码。
/// </summary>
public sealed class HardwareException : Exception
{
    /// <summary>发生故障的设备名（如「虚拟运控卡」）。</summary>
    public string DeviceName { get; }

    /// <summary>硬件错误码（运控卡停止原因 / SDK 返回码），含义由具体设备定义。</summary>
    public int ErrorCode { get; }

    /// <summary>构造无设备上下文信息的硬件异常（错误码按 0 处理）。</summary>
    /// <param name="message">面向操作工的可读描述。</param>
    public HardwareException(string message)
        : this("", 0, message)
    {
    }

    /// <summary>构造硬件异常。</summary>
    /// <param name="deviceName">设备名。</param>
    /// <param name="errorCode">错误码。</param>
    /// <param name="message">面向操作工的可读描述。</param>
    public HardwareException(string deviceName, int errorCode, string message)
        : base(message)
    {
        DeviceName = deviceName;
        ErrorCode = errorCode;
    }

    /// <summary>构造带内部异常的硬件异常。</summary>
    /// <param name="deviceName">设备名。</param>
    /// <param name="errorCode">错误码。</param>
    /// <param name="message">面向操作工的可读描述。</param>
    /// <param name="innerException">内部异常。</param>
    public HardwareException(string deviceName, int errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        DeviceName = deviceName;
        ErrorCode = errorCode;
    }
}
