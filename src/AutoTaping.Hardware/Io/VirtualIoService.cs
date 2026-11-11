// 职责：虚拟 IO 服务——内存点位表 + 手动置位（模拟传感器） + 工程师强制输出
// 层：Hardware/Io
// 依赖：IIoService（Core/Abstractions）、ILogger

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Hardware.Io;

/// <summary>
/// <see cref="IIoService"/> 的虚拟实现。DI/DO 点位全部存内存，支持 <see cref="SetDi"/> 手动置位
/// 模拟传感器输入（docs/02 第 2 节），支持工程师强制输出。
/// </summary>
public sealed class VirtualIoService : IIoService
{
    private readonly Dictionary<string, bool> _di = new();
    private readonly Dictionary<string, bool> _do = new();
    private readonly Dictionary<string, bool> _forced = new();
    private readonly object _sync = new();
    private readonly ILogger<VirtualIoService> _log;

    /// <summary>构造虚拟 IO 服务（全部点位初始为 OFF）。</summary>
    /// <param name="diPoints">输入点位名集合（来自 appsettings 的 <c>Io:DiMap</c> 键）。</param>
    /// <param name="doPoints">输出点位名集合（来自 appsettings 的 <c>Io:DoMap</c> 键）。</param>
    /// <param name="log">日志。</param>
    public VirtualIoService(IEnumerable<string> diPoints, IEnumerable<string> doPoints, ILogger<VirtualIoService> log)
        : this(diPoints, doPoints, null, log)
    {
    }

    /// <summary>构造虚拟 IO 服务，并按 <paramref name="initialDiStates"/> 预置输入点位。</summary>
    /// <param name="diPoints">输入点位名集合。</param>
    /// <param name="doPoints">输出点位名集合。</param>
    /// <param name="initialDiStates">
    /// 输入点位的初始状态（来自 <c>Io:InitialDiStates</c>）。安全回路是常闭/常开明确的信号，
    /// 若全部按 OFF 预置，则 X05 安全门、X06 急停、X07 气压会被误判为故障状态。
    /// 传 <see langword="null"/> 表示全部 OFF。
    /// </param>
    /// <param name="log">日志。</param>
    public VirtualIoService(IEnumerable<string> diPoints, IEnumerable<string> doPoints,
                            IReadOnlyDictionary<string, bool>? initialDiStates,
                            ILogger<VirtualIoService> log)
    {
        _log = log;

        foreach (var point in diPoints)
        {
            _di[point] = initialDiStates is not null
                         && initialDiStates.TryGetValue(point, out var initial)
                         && initial;
        }

        foreach (var point in doPoints)
        {
            _do[point] = false;
        }
    }

    /// <inheritdoc />
    public bool ReadDi(string point)
    {
        lock (_sync) { return _di.TryGetValue(point, out var value) && value; }
    }

    /// <inheritdoc />
    public bool ReadDo(string point)
    {
        lock (_sync)
        {
            if (_forced.TryGetValue(point, out var forced))
            {
                return forced;
            }

            return _do.TryGetValue(point, out var value) && value;
        }
    }

    /// <inheritdoc />
    public void WriteDo(string point, bool on)
    {
        lock (_sync)
        {
            if (!_do.ContainsKey(point))
            {
                return;
            }

            _do[point] = on;

            // 虚拟模式气路反馈：真空阀 Y00 开/关联动真空吸住传感器 X03（真实设备是气路物理联动）。
            // 用于让自动流程的「开真空等待吸住」步骤在无硬件时也能通过。
            if (string.Equals(point, IoPointNames.VacuumValve, StringComparison.Ordinal)
                && _di.ContainsKey(IoPointNames.VacuumSensor))
            {
                _di[IoPointNames.VacuumSensor] = on;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, bool> ReadAllDi()
    {
        lock (_sync) { return new Dictionary<string, bool>(_di); }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, bool> ReadAllDo()
    {
        lock (_sync)
        {
            var snapshot = new Dictionary<string, bool>(_do);
            foreach (var (point, value) in _forced)
            {
                snapshot[point] = value;
            }

            return snapshot;
        }
    }

    /// <inheritdoc />
    public void ForceOutput(string point, bool on)
    {
        lock (_sync)
        {
            if (!_do.ContainsKey(point))
            {
                return;
            }

            _forced[point] = on;
            _log.LogWarning("IO 强制输出：{Point} = {Value}", point, on);
        }
    }

    /// <inheritdoc />
    public void ClearAllForcedOutput()
    {
        lock (_sync)
        {
            if (_forced.Count > 0)
            {
                _log.LogInformation("清除全部 IO 强制输出（{Count} 个点位）", _forced.Count);
            }

            _forced.Clear();
        }
    }

    /// <inheritdoc />
    public bool IsForced(string point)
    {
        lock (_sync) { return _forced.ContainsKey(point); }
    }

    /// <summary>
    /// 手动置位输入点位，用于模拟传感器信号（如「上料位有料」「安全门闭合」）。
    /// </summary>
    /// <param name="point">点位名，如 <c>"X00"</c>。</param>
    /// <param name="value">新状态。</param>
    public void SetDi(string point, bool value)
    {
        lock (_sync)
        {
            if (!_di.ContainsKey(point))
            {
                return;
            }

            _di[point] = value;
        }
    }
}
