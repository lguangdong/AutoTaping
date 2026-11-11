// 职责：报警服务——统一触发入口 + 5 秒去重 + 自动恢复 + 人工确认 + 历史查询 + CSV 导出
// 层：Core/Services
// 依赖：IAlarmRepository、IClock（Core/Abstractions）、AlarmLevel/AlarmRecord/AlarmRaisedEventArgs（Core/Models）

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Core.Services;

/// <summary>
/// <see cref="IAlarmService"/> 的默认实现。规则见 <c>docs/09-报警与参数清单.md</c> 第三节：
/// <list type="bullet">
/// <item>去重窗口：同 <c>Code</c> 在 5 秒内重复触发不新增记录，只累加计数（本类在内存里计数）。</item>
/// <item>自动恢复：条件消失时由协调器调用 <see cref="Recover"/> 写恢复时间，报警从「当前」列表移除。</item>
/// <item>需人工确认：等级 3 报警确认后才允许复位（由状态机守卫判定，本类只负责确认动作）。</item>
/// <item>不可自动恢复：<c>AL100 / AL101 / AL103 / AL105</c> 必须人工处理。</item>
/// </list>
/// 报警写库失败不得再抛异常，否则会掩盖原始故障（01-架构设计 第 6 节）。
/// </summary>
public sealed class AlarmService : IAlarmService
{
    /// <summary>去重窗口（秒），09 文档第三节。</summary>
    private const int DedupWindowSeconds = 5;

    /// <summary>不可自动恢复的报警码（09 文档第三节）。</summary>
    private static readonly HashSet<string> NonRecoverableCodes = new(StringComparer.Ordinal)
    {
        "AL100", "AL101", "AL103", "AL105"
    };

    private readonly IAlarmRepository _alarms;
    private readonly IClock _clock;
    private readonly ILogger<AlarmService> _log;

    private readonly object _sync = new();

    /// <summary>去重窗口状态：报警码 → 最近一次触发时间与窗口内累计次数。</summary>
    private readonly Dictionary<string, RecentEntry> _recent = new(StringComparer.Ordinal);

    /// <summary>当前生效中的报警码集合（用于把 <see cref="Recover"/> 的数据库查询压到「确有报警」时）。</summary>
    private readonly HashSet<string> _activeCodes = new(StringComparer.Ordinal);

    /// <summary>构造报警服务，并把数据库中残留的生效报警纳入恢复跟踪。</summary>
    /// <param name="alarms">报警仓储。</param>
    /// <param name="clock">时钟（报警发生时间）。</param>
    /// <param name="log">日志。</param>
    public AlarmService(IAlarmRepository alarms, IClock clock, ILogger<AlarmService> log)
    {
        _alarms = alarms;
        _clock = clock;
        _log = log;

        // 上次运行遗留的未恢复报警也要能被 Recover / 确认（读库失败不阻断启动）
        RefreshActiveCodes();
    }

    /// <inheritdoc />
    public event EventHandler<AlarmRaisedEventArgs>? AlarmRaised;

    /// <inheritdoc />
    public void Raise(string code, AlarmLevel level, string message, string source = "")
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            // 报警码是设备的「契约」（09 文档开关说明），空码说明调用方写错了，必须留痕
            _log.LogError("报警码为空，已忽略：{Message}，来源 {Source}", message, source);
            return;
        }

        AlarmRecord record;

        lock (_sync)
        {
            var now = _clock.Now;

            if (_recent.TryGetValue(code, out var recent)
                && (now - recent.LastTime).TotalSeconds < DedupWindowSeconds)
            {
                recent.LastTime = now;
                recent.Count++;

                // 告警抖动会刷出几百条记录（09 文档第三节），去重命中只记 Debug
                _log.LogDebug("报警 {Code} 在 {Window}s 去重窗口内重复触发（第 {Count} 次），不新增记录",
                              code, DedupWindowSeconds, recent.Count);
                return;
            }

            record = new AlarmRecord
            {
                Code = code,
                Level = level,
                Message = message,
                Source = source,
                StartTime = now
            };

            try
            {
                _alarms.Insert(record);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "报警 {Code} 写入失败：{Message}", code, message);
                return;
            }

            _recent[code] = new RecentEntry(now, 1);
            _activeCodes.Add(code);
        }

        // 报警触发属关键事件（10-编码约定 7.3）：级别映射到日志级别，严重用 Error
        if (level >= AlarmLevel.Critical)
        {
            _log.LogError("报警触发：{Code}（{Level}）{Message}，来源 {Source}", code, level, message, source);
        }
        else
        {
            _log.LogWarning("报警触发：{Code}（{Level}）{Message}，来源 {Source}", code, level, message, source);
        }

        AlarmRaised?.Invoke(this, new AlarmRaisedEventArgs(record, 1));
    }

    /// <inheritdoc />
    public bool HasUnackedCritical() => _alarms.CountUnacked((int)AlarmLevel.Critical) > 0;

    /// <inheritdoc />
    public void CloseAllActive()
    {
        try
        {
            _alarms.CloseAllActive();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "关闭生效中报警失败");
        }

        lock (_sync)
        {
            _activeCodes.Clear();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AlarmRecord> GetActiveAlarms() => _alarms.QueryActive();

    /// <inheritdoc />
    public IReadOnlyList<AlarmRecord> QueryHistory(DateTime from, DateTime to) => _alarms.Query(from, to);

    /// <inheritdoc />
    public void Ack(int id, string user)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);

        _alarms.Ack(id, user);
        RefreshActiveCodes();
        _log.LogInformation("确认报警 {AlarmId}，确认人 {User}", id, user);
    }

    /// <inheritdoc />
    public int AckAll(string user)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);

        var active = _alarms.QueryActive();
        var acked = 0;

        foreach (var record in active)
        {
            try
            {
                _alarms.Ack(record.Id, user);
                acked++;
            }
            catch (KeyNotFoundException ex)
            {
                // 并发下可能被别的界面先确认掉：跳过即可，不影响其余记录
                _log.LogWarning(ex, "确认报警 {AlarmId} 时发现已确认或不存在，已跳过", record.Id);
            }
        }

        RefreshActiveCodes();
        _log.LogInformation("全部确认报警：共 {Count} 条，确认人 {User}", acked, user);
        return acked;
    }

    /// <inheritdoc />
    public void Recover(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        if (NonRecoverableCodes.Contains(code))
        {
            _log.LogDebug("报警 {Code} 不可自动恢复，需人工处理后确认", code);
            return;
        }

        lock (_sync)
        {
            if (!_activeCodes.Contains(code))
            {
                return;
            }
        }

        try
        {
            var active = _alarms.QueryActive().FirstOrDefault(record => string.Equals(record.Code, code, StringComparison.Ordinal));
            if (active is null)
            {
                RemoveActiveCode(code);
                return;
            }

            _alarms.Close(active.Id, _clock.Now);
            RemoveActiveCode(code);
            _log.LogInformation("报警 {Code} 条件已消失，自动恢复（记录 {AlarmId}）", code, active.Id);
        }
        catch (Exception ex)
        {
            // 恢复失败不影响设备继续运行，下次监视周期会再试
            _log.LogError(ex, "报警 {Code} 自动恢复失败", code);
        }
    }

    /// <inheritdoc />
    public void ExportCsv(DateTime from, DateTime to, string path) => _alarms.ExportCsv(from, to, path);

    /// <summary>重新从数据库同步「生效中的报警码」集合（确认 / 启动后调用）。</summary>
    private void RefreshActiveCodes()
    {
        try
        {
            var active = _alarms.QueryActive();

            lock (_sync)
            {
                _activeCodes.Clear();
                foreach (var record in active)
                {
                    _activeCodes.Add(record.Code);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "读取生效报警失败，自动恢复可能延后");
        }
    }

    private void RemoveActiveCode(string code)
    {
        lock (_sync)
        {
            _activeCodes.Remove(code);
            _recent.Remove(code);
        }
    }

    /// <summary>去重窗口内的一次报警状态。</summary>
    private sealed class RecentEntry
    {
        public RecentEntry(DateTime lastTime, int count)
        {
            LastTime = lastTime;
            Count = count;
        }

        /// <summary>最近一次触发时间。</summary>
        public DateTime LastTime { get; set; }

        /// <summary>窗口内累计触发次数。</summary>
        public int Count { get; set; }
    }
}
