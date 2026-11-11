// 职责：系统自检——磁盘剩余空间（AL501）、数据库可写性（AL502）与硬件状态巡检，条件恢复时自动关闭报警
// 层：Core/Services
// 依赖：IAlarmService、ISystemStateRepository、IDeviceMonitor、IClock（Core/Abstractions）、
//       SelfCheckOptions/SelfCheckReport/IoPointNames/AlarmLevel（Core/Models）

using System.Globalization;
using System.IO;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Core.Services;

/// <summary>
/// <see cref="ISelfCheckService"/> 的默认实现（11-开发任务分解 S9：启动自检覆盖磁盘、数据库、硬件）。
/// <para>
/// 报警策略：磁盘低于下限报 AL501、数据库写探针失败报 AL502，二者都按「状态变化才报一次」处理
/// （避免每分钟刷一条，09 文档第三节的报警风暴保护）；条件恢复时调用 <c>Recover</c> 自动关闭。
/// 单次检查失败不得抛异常，否则会掩盖设备原有的故障（01-架构设计 第 6 节）。
/// </para>
/// </summary>
public sealed class SelfCheckService : ISelfCheckService
{
    /// <summary>数据库写探针在 SystemState 表中的键名。</summary>
    private const string DatabaseProbeKey = "DatabaseProbeAt";

    /// <summary>写探针的值（内容无意义，只要能成功写入就说明库可写）。</summary>
    private const string DatabaseProbeValue = "ok";

    /// <summary>1 GB 的字节数（磁盘剩余空间换算）。</summary>
    private const double BytesPerGb = 1024.0 * 1024.0 * 1024.0;

    /// <summary>报警码：磁盘空间不足（09 文档第五节）。</summary>
    private const string DiskSpaceAlarm = "AL501";

    /// <summary>报警码：数据库写入失败（09 文档第五节）。</summary>
    private const string DatabaseAlarm = "AL502";

    /// <summary>磁盘报警来源。</summary>
    private const string DiskAlarmSource = "系统";

    /// <summary>数据库报警来源。</summary>
    private const string DatabaseAlarmSource = "数据库";

    private readonly IAlarmService _alarms;
    private readonly ISystemStateRepository _stateRepository;
    private readonly IDeviceMonitor _monitor;
    private readonly IClock _clock;
    private readonly SelfCheckOptions _options;
    private readonly ILogger<SelfCheckService> _log;

    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _diskAlarmActive;
    private bool _databaseAlarmActive;
    private bool _disposed;

    /// <summary>构造自检服务。</summary>
    /// <param name="alarms">报警服务（AL501 / AL502）。</param>
    /// <param name="stateRepository">系统状态仓储（写探针）。</param>
    /// <param name="monitor">设备快照（硬件状态）。</param>
    /// <param name="clock">时钟。</param>
    /// <param name="options">自检配置。</param>
    /// <param name="log">日志。</param>
    public SelfCheckService(
        IAlarmService alarms,
        ISystemStateRepository stateRepository,
        IDeviceMonitor monitor,
        IClock clock,
        SelfCheckOptions options,
        ILogger<SelfCheckService> log)
    {
        _alarms = alarms;
        _stateRepository = stateRepository;
        _monitor = monitor;
        _clock = clock;
        _options = options;
        _log = log;
    }

    /// <inheritdoc />
    public SelfCheckReport Run()
    {
        var now = _clock.Now;

        var diskOk = CheckDiskSpace(out var freeDiskGb);
        var databaseOk = CheckDatabaseWritable();

        var snapshot = _monitor.Current;
        var report = new SelfCheckReport(
            diskOk,
            freeDiskGb,
            databaseOk,
            snapshot.MotionConnected,
            snapshot.CameraConnected,
            snapshot.Di(IoPointNames.AirPressureOk),
            now);

        _log.LogInformation(
            "系统自检：磁盘 {DiskOk}（剩余 {FreeGb:F2}GB），数据库可写 {DbOk}，运控卡 {Motion}，相机 {Camera}，气压 {Air}",
            diskOk, freeDiskGb, databaseOk, report.MotionConnected, report.CameraConnected, report.AirPressureOk);

        return report;
    }

    /// <inheritdoc />
    public void Start()
    {
        if (_disposed || _loopTask is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <inheritdoc />
    public void Stop()
    {
        var cts = _cts;
        if (cts is null)
        {
            return;
        }

        _cts = null;
        cts.Cancel();
        cts.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    /// <summary>周期巡检：每分钟检查一次磁盘与数据库，条件变化时报警 / 恢复。</summary>
    private async Task LoopAsync(CancellationToken ct)
    {
        // 配置非法（0 或负数）时退化为 60s，避免 PeriodicTimer 抛参数异常
        var seconds = _options.IntervalSeconds > 0 ? _options.IntervalSeconds : 60;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                Run();
            }
        }
        catch (OperationCanceledException)
        {
            // 应用退出时的正常路径
            _log.LogDebug("系统自检巡检已停止");
        }
        catch (Exception ex)
        {
            // 巡检线程不能死：记录后退出（下次启动重新开始），不得静默失败
            _log.LogError(ex, "系统自检巡检异常终止");
        }
    }

    /// <summary>检查磁盘剩余空间（09 文档 AL501）。无法判断剩余空间时不报警，只记警告。</summary>
    private bool CheckDiskSpace(out double freeDiskGb)
    {
        freeDiskGb = -1;

        try
        {
            var dataDirectory = ResolveDataDirectory();
            var root = Path.GetPathRoot(dataDirectory);
            if (string.IsNullOrEmpty(root))
            {
                _log.LogWarning("无法确定数据目录 {Directory} 所在分区，跳过磁盘空间检查", dataDirectory);
                return true;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                _log.LogWarning("数据目录所在分区 {Root} 未就绪，跳过磁盘空间检查", root);
                return true;
            }

            freeDiskGb = drive.AvailableFreeSpace / BytesPerGb;
            var ok = freeDiskGb >= _options.MinFreeDiskGb;

            ApplyDiskAlarm(ok, freeDiskGb);
            return ok;
        }
        catch (Exception ex)
        {
            // 网络盘 / 权限问题读不到剩余空间：不误报 AL501，但要留痕
            _log.LogWarning(ex, "读取磁盘剩余空间失败，本次跳过磁盘检查");
            return true;
        }
    }

    /// <summary>用 SystemState 写探针检查数据库是否可写（09 文档 AL502）。</summary>
    private bool CheckDatabaseWritable()
    {
        var ok = true;

        try
        {
            _stateRepository.Set(DatabaseProbeKey, DatabaseProbeValue);
        }
        catch (Exception ex)
        {
            ok = false;
            _log.LogError(ex, "数据库写探针失败（库只读 / 磁盘满 / 文件被占用）");
        }

        ApplyDatabaseAlarm(ok);
        return ok;
    }

    /// <summary>磁盘报警：状态由正常变异常时报一次，恢复后自动关闭。</summary>
    private void ApplyDiskAlarm(bool ok, double freeDiskGb)
    {
        lock (_sync)
        {
            if (!ok && !_diskAlarmActive)
            {
                _diskAlarmActive = true;
                _alarms.Raise(
                    DiskSpaceAlarm,
                    AlarmLevel.Warning,
                    $"磁盘空间不足：剩余 {freeDiskGb.ToString("F2", CultureInfo.InvariantCulture)} GB" +
                    $"（下限 {_options.MinFreeDiskGb.ToString("F1", CultureInfo.InvariantCulture)} GB），" +
                    "请清理日志、备份或旧数据",
                    DiskAlarmSource);
                _log.LogWarning("磁盘空间不足：剩余 {FreeGb:F2}GB，阈值 {MinGb:F1}GB", freeDiskGb, _options.MinFreeDiskGb);
                return;
            }

            if (ok)
            {
                // 不判断 _diskAlarmActive：软件重启后内存标记已丢失，但库里可能还留着上次的 AL501，
                // 条件正常时同样要关闭它（Recover 对没有生效报警的情况是安全无操作）
                _diskAlarmActive = false;
                _alarms.Recover(DiskSpaceAlarm);
            }
        }
    }

    /// <summary>数据库报警：状态由正常变异常时报一次，恢复后自动关闭。</summary>
    private void ApplyDatabaseAlarm(bool ok)
    {
        lock (_sync)
        {
            if (!ok && !_databaseAlarmActive)
            {
                _databaseAlarmActive = true;
                _alarms.Raise(
                    DatabaseAlarm,
                    AlarmLevel.Warning,
                    "数据库写入失败：产量与报警可能无法保存，请检查磁盘空间、文件权限与占用",
                    DatabaseAlarmSource);
                return;
            }

            if (ok)
            {
                // 同磁盘：重启后也要把上次遗留的 AL502 关掉（库现在可写就说明条件已消失）
                _databaseAlarmActive = false;
                _alarms.Recover(DatabaseAlarm);
            }
        }
    }

    /// <summary>数据目录绝对路径（相对路径按程序目录解析）。</summary>
    private string ResolveDataDirectory()
        => Path.IsPathRooted(_options.DataDirectory)
            ? _options.DataDirectory
            : Path.Combine(AppContext.BaseDirectory, _options.DataDirectory);
}
