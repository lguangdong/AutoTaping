// 职责：真实 IO 服务——通过运控卡（LTDMC.dll）读写 DI/DO 点位，支持工程师强制输出
// 层：Hardware/Io
// 依赖：LTDMC.dll（经 Native/LeadshineApi）、IIoService（Core/Abstractions）、ILogger

using AutoTaping.Core.Abstractions;
using AutoTaping.Hardware.Motion.Native;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Hardware.Io;

/// <summary>
/// <see cref="IIoService"/> 的真实实现，DI/DO 走运控卡（02 文档第 2/3 节）。
/// 点位号约定：<c>X</c> 前缀为输入（DI），<c>Y</c> 前缀为输出（DO），后两位为点位号（00~07），
/// 对应 <c>dmc_read_inbit</c> / <c>dmc_write_outbit</c> 的 bitNo。
/// 与 <see cref="AutoTaping.Hardware.Motion.LeadshineMotionCard"/> 共用同一把 DLL 锁（02 文档第 7 节）。
/// </summary>
public sealed class MotionCardIoService : IIoService
{
    private readonly ushort _cardNo;
    private readonly Dictionary<string, bool> _di = new();
    private readonly Dictionary<string, bool> _do = new();
    private readonly Dictionary<string, bool> _forced = new();
    private readonly SemaphoreSlim _dllLock;
    private readonly ILogger<MotionCardIoService> _log;
    private readonly object _sync = new();

    /// <summary>构造真实 IO 服务。</summary>
    /// <param name="cardNo">板卡号。</param>
    /// <param name="diPoints">输入点位名集合（来自 appsettings 的 <c>Io:DiMap</c> 键）。</param>
    /// <param name="doPoints">输出点位名集合（来自 appsettings 的 <c>Io:DoMap</c> 键）。</param>
    /// <param name="dllLock">与运控卡共用的 DLL 串行化锁。</param>
    /// <param name="log">日志。</param>
    public MotionCardIoService(ushort cardNo, IEnumerable<string> diPoints, IEnumerable<string> doPoints,
                               SemaphoreSlim dllLock, ILogger<MotionCardIoService> log)
    {
        _cardNo = cardNo;
        _dllLock = dllLock;
        _log = log;

        foreach (var point in diPoints)
        {
            _di[point] = false;
        }

        foreach (var point in doPoints)
        {
            _do[point] = false;
        }
    }

    /// <inheritdoc />
    public bool ReadDi(string point)
    {
        if (!TryParseBit(point, out var prefix, out var bitNo) || prefix != 'X')
        {
            return false;
        }

        _dllLock.Wait();
        try
        {
            var value = LeadshineApi.dmc_read_inbit(_cardNo, (ushort)bitNo);
            var on = value != 0;
            lock (_sync) { _di[point] = on; }
            return on;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "读取输入点位 {Point} 失败", point);
            lock (_sync) { return _di.TryGetValue(point, out var cached) && cached; }
        }
        finally
        {
            _dllLock.Release();
        }
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
        }

        if (!TryParseBit(point, out var prefix, out var bitNo) || prefix != 'Y')
        {
            return false;
        }

        _dllLock.Wait();
        try
        {
            var value = LeadshineApi.dmc_read_outbit(_cardNo, (ushort)bitNo);
            return value != 0;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "读取输出点位 {Point} 失败", point);
            lock (_sync) { return _do.TryGetValue(point, out var cached) && cached; }
        }
        finally
        {
            _dllLock.Release();
        }
    }

    /// <inheritdoc />
    public void WriteDo(string point, bool on)
    {
        if (!TryParseBit(point, out var prefix, out var bitNo) || prefix != 'Y')
        {
            return;
        }

        _dllLock.Wait();
        try
        {
            Check(LeadshineApi.dmc_write_outbit(_cardNo, (ushort)bitNo, (ushort)(on ? 1 : 0)), "写输出点位", point);
            lock (_sync) { _do[point] = on; }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "写输出点位 {Point} 失败", point);
        }
        finally
        {
            _dllLock.Release();
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, bool> ReadAllDi()
    {
        var snapshot = new Dictionary<string, bool>();
        foreach (var point in _di.Keys)
        {
            snapshot[point] = ReadDi(point);
        }

        return snapshot;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, bool> ReadAllDo()
    {
        var snapshot = new Dictionary<string, bool>();
        foreach (var point in _do.Keys)
        {
            snapshot[point] = ReadDo(point);
        }

        return snapshot;
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
        }

        // 强制输出是工程师直接干预硬件：物理写入输出点位（与 Virtual 一致记录强制状态）。
        WriteDo(point, on);
        _log.LogWarning("IO 强制输出：{Point} = {Value}", point, on);
    }

    /// <inheritdoc />
    public void ClearAllForcedOutput()
    {
        List<KeyValuePair<string, bool>> toRestore;
        lock (_sync)
        {
            toRestore = _forced.Select(kv => new KeyValuePair<string, bool>(kv.Key, _do.TryGetValue(kv.Key, out var v) && v))
                                .ToList();
            if (_forced.Count > 0)
            {
                _log.LogInformation("清除全部 IO 强制输出（{Count} 个点位）", _forced.Count);
            }

            _forced.Clear();
        }

        // 清除强制后恢复各点位的正常受控值（02 文档第 2 节：退出手动页必须清除强制输出）。
        foreach (var (point, normalValue) in toRestore)
        {
            WriteDo(point, normalValue);
        }
    }

    /// <inheritdoc />
    public bool IsForced(string point)
    {
        lock (_sync) { return _forced.ContainsKey(point); }
    }

    /// <summary>解析点位号（如 <c>"X05"</c> → 前缀 <c>'X'</c> + 位号 5）。非法点位返回 false。</summary>
    private static bool TryParseBit(string point, out char prefix, out int bitNo)
    {
        prefix = '\0';
        bitNo = -1;

        if (string.IsNullOrWhiteSpace(point) || point.Length < 2)
        {
            return false;
        }

        prefix = char.ToUpperInvariant(point[0]);
        if (prefix is not ('X' or 'Y'))
        {
            return false;
        }

        return int.TryParse(point.AsSpan(1), out bitNo) && bitNo >= 0;
    }

    /// <summary>校验写操作返回码：非 0 抛异常（写失败即掉线，上抛由上层记录）。</summary>
    private void Check(ushort ret, string operation, string point)
    {
        if (ret != 0)
        {
            throw new AutoTaping.Core.Exceptions.HardwareException(
                "雷赛运控卡", ret, $"IO {operation} 失败，点位={point}，错误码={ret}");
        }
    }
}
