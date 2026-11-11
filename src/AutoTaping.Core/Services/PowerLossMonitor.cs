// 职责：断电（非正常退出）检测——读写 SystemState 的 LastShutdownClean 键
// 层：Core/Services
// 依赖：ISystemStateRepository（Core/Abstractions）、ILogger

using AutoTaping.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Core.Services;

/// <summary>
/// 断电检测。实现 <c>docs/06-数据模型与数据库.md</c> 第 8 节的约定：
/// 启动时读 <c>LastShutdownClean</c>，只要不等于 <c>"1"</c> 就说明上次非正常退出；
/// 读完立刻置 <c>"0"</c>（表示"本次运行尚未正常退出"），正常退出时由 App 写回 <c>"1"</c>。
/// </summary>
public sealed class PowerLossMonitor
{
    /// <summary>系统状态表中记录"上次是否正常退出"的键名。</summary>
    public const string LastShutdownCleanKey = "LastShutdownClean";

    /// <summary>正常退出的标记值。</summary>
    private const string CleanValue = "1";

    /// <summary>运行中（尚未正常退出）的标记值。</summary>
    private const string RunningValue = "0";

    private readonly ISystemStateRepository _stateRepository;
    private readonly ILogger<PowerLossMonitor> _log;

    /// <summary>构造断电检测器。</summary>
    /// <param name="stateRepository">系统状态键值仓储。</param>
    /// <param name="log">日志。</param>
    public PowerLossMonitor(ISystemStateRepository stateRepository, ILogger<PowerLossMonitor> log)
    {
        _stateRepository = stateRepository;
        _log = log;
    }

    /// <summary>
    /// 检测上次是否非正常退出，并把状态标记为"运行中"。
    /// </summary>
    /// <returns>
    /// 上次非正常退出返回 <see langword="true"/>（调用方据此补记停机原因与报警 AL503，由 S7 的报警服务负责）；
    /// 首次启动（表中无该键）或上次正常退出返回 <see langword="false"/>。
    /// </returns>
    public bool DetectAndMarkRunning()
    {
        var lastValue = _stateRepository.Get(LastShutdownCleanKey);

        // 首次启动没有该键：不算异常，避免新机器一装好就报 AL503
        var wasUnclean = lastValue is not null && lastValue != CleanValue;

        if (wasUnclean)
        {
            _log.LogWarning("检测到上次非正常退出（LastShutdownClean={LastValue}），需补记停机原因并报警 AL503",
                            lastValue);
        }
        else if (lastValue is null)
        {
            _log.LogInformation("首次启动，初始化系统状态键 {Key}", LastShutdownCleanKey);
        }

        _stateRepository.Set(LastShutdownCleanKey, RunningValue);
        return wasUnclean;
    }

    /// <summary>标记本次退出为正常退出。必须在 App 退出流程最后调用一次。</summary>
    public void MarkCleanShutdown()
    {
        _stateRepository.Set(LastShutdownCleanKey, CleanValue);
        _log.LogInformation("已写入正常退出标记 {Key}={Value}", LastShutdownCleanKey, CleanValue);
    }
}
