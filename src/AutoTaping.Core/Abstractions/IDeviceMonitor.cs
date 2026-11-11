// 职责：设备采集线程抽象——200ms 轮询轴位置与 IO，产出只读快照供界面消费
// 层：Core/Abstractions
// 依赖：DeviceSnapshot（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.Core.Abstractions;

/// <summary>
/// 采集线程（01-架构设计 第 3 节：独立于 UI 线程与流程线程，200ms 周期）。
/// 界面只读 <see cref="Current"/>，不得直接调用运控卡；采集异常不得让线程退出。
/// </summary>
public interface IDeviceMonitor : IDisposable
{
    /// <summary>最近一帧快照。采集线程整体替换引用，读取方无需加锁。</summary>
    DeviceSnapshot Current { get; }

    /// <summary>启动采集（重复调用安全）。</summary>
    void Start();

    /// <summary>停止采集（重复调用安全）。</summary>
    void Stop();
}
