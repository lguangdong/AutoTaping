// 职责：SelfCheckService 单元测试——磁盘不足报 AL501 且恢复后自动关闭、数据库不可写报 AL502（S9 启动自检）
// 层：Tests
// 依赖：AutoTaping.Core（SelfCheckService、AlarmService）、AutoTaping.Data、Tests/Infrastructure/TestDatabase

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using AutoTaping.Core.Services;
using AutoTaping.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTaping.Tests;

/// <summary>
/// <see cref="SelfCheckService"/> 的测试。硬件状态由替身 <see cref="FakeDeviceMonitor"/> 提供，
/// 报警走真实的 <see cref="AlarmService"/> + 临时数据库，便于断言报警是否真的落库与恢复。
/// </summary>
public sealed class SelfCheckServiceTests
{
    [Fact]
    public void Run_DiskBelowThreshold_RaisesAl501_AndRecoversWhenSpaceIsEnough()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var alarms = CreateAlarmService(database);

        // 阈值设成不可能满足的 1PB → 必然触发 AL501；随后把阈值改回 0 模拟「清理磁盘后恢复」
        var options = new SelfCheckOptions { MinFreeDiskGb = 1_000_000 };
        var service = CreateService(database, alarms, options);

        var failed = service.Run();

        Assert.False(failed.DiskSpaceOk);
        Assert.Contains(alarms.GetActiveAlarms(), record => record.Code == "AL501");

        options.MinFreeDiskGb = 0;
        var passed = service.Run();

        Assert.True(passed.DiskSpaceOk);
        Assert.DoesNotContain(alarms.GetActiveAlarms(), record => record.Code == "AL501");
    }

    [Fact]
    public void Run_DatabaseNotWritable_RaisesAl502()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var alarms = CreateAlarmService(database);
        var service = new SelfCheckService(
            alarms,
            new ThrowingStateRepository(),
            new FakeDeviceMonitor(),
            database.Clock,
            new SelfCheckOptions(),
            NullLogger<SelfCheckService>.Instance);

        var report = service.Run();

        Assert.False(report.DatabaseWritable);
        Assert.Contains(alarms.GetActiveAlarms(), record => record.Code == "AL502");
    }

    [Fact]
    public void Run_ReportsHardwareStateFromSnapshot()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var snapshot = new DeviceSnapshot
        {
            MotionConnected = true,
            CameraConnected = false,
            DiStates = new Dictionary<string, bool>(StringComparer.Ordinal) { [IoPointNames.AirPressureOk] = true }
        };

        var service = new SelfCheckService(
            CreateAlarmService(database),
            database.CreateSystemStateRepository(),
            new FakeDeviceMonitor(snapshot),
            database.Clock,
            new SelfCheckOptions(),
            NullLogger<SelfCheckService>.Instance);

        var report = service.Run();

        Assert.True(report.DatabaseWritable);
        Assert.True(report.MotionConnected);
        Assert.False(report.CameraConnected);
        Assert.True(report.AirPressureOk);
        Assert.False(report.HardwareOk);
    }

    private static SelfCheckService CreateService(TestDatabase database, IAlarmService alarms, SelfCheckOptions options)
        => new(alarms,
               database.CreateSystemStateRepository(),
               new FakeDeviceMonitor(),
               database.Clock,
               options,
               NullLogger<SelfCheckService>.Instance);

    private static AlarmService CreateAlarmService(TestDatabase database)
        => new(database.CreateAlarmRepository(), database.Clock, NullLogger<AlarmService>.Instance);

    /// <summary>固定快照的采集线程替身。</summary>
    private sealed class FakeDeviceMonitor : IDeviceMonitor
    {
        private readonly DeviceSnapshot _snapshot;

        public FakeDeviceMonitor()
            : this(DeviceSnapshot.Empty)
        {
        }

        public FakeDeviceMonitor(DeviceSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public DeviceSnapshot Current => _snapshot;

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>写探针永远失败的仓储替身（模拟数据库只读）。</summary>
    private sealed class ThrowingStateRepository : ISystemStateRepository
    {
        public string? Get(string key) => null;

        public void Set(string key, string value)
            => throw new SqliteException("attempt to write a readonly database", 8);
    }
}
