// 职责：AlarmService 单元测试——5 秒去重、确认、全部确认、自动恢复、不可恢复码
// 层：Tests
// 依赖：TestDatabase、AutoTaping.Core.Services、AutoTaping.Core.Models、AutoTaping.Core.Abstractions

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using AutoTaping.Core.Services;
using AutoTaping.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTaping.Tests;

/// <summary>
/// 报警服务测试，对应 S7 验收标准第 3、4 条（5 秒去重只产生 1 条；确认后从当前列表消失并进入历史）。
/// </summary>
public sealed class AlarmServiceTests
{
    [Fact]
    public void Raise_ThreeTimesWithinFiveSeconds_ProducesSingleRecord()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var clock = new FakeClock(new DateTime(2026, 11, 8, 10, 0, 0));
        var repository = database.CreateAlarmRepository();
        var service = new AlarmService(repository, clock, NullLogger<AlarmService>.Instance);

        // 触发 3 次，间隔 1s（S7 验收标准第 3 条）
        service.Raise("AL301", AlarmLevel.Critical, "真空吸不住", "贴附头");
        clock.Advance(TimeSpan.FromSeconds(1));
        service.Raise("AL301", AlarmLevel.Critical, "真空吸不住", "贴附头");
        clock.Advance(TimeSpan.FromSeconds(1));
        service.Raise("AL301", AlarmLevel.Critical, "真空吸不住", "贴附头");

        var all = repository.Query(clock.Now.AddMinutes(-10), clock.Now.AddMinutes(10));
        Assert.Single(all);

        // 去重窗口内不新增记录，但当前列表与严重报警计数照常反映
        Assert.Single(service.GetActiveAlarms());
        Assert.True(service.HasUnackedCritical());
    }

    [Fact]
    public void Raise_AfterDedupWindow_ProducesNewRecord()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var clock = new FakeClock(new DateTime(2026, 11, 8, 10, 0, 0));
        var repository = database.CreateAlarmRepository();
        var service = new AlarmService(repository, clock, NullLogger<AlarmService>.Instance);

        service.Raise("AL301", AlarmLevel.Critical, "真空吸不住", "贴附头");
        clock.Advance(TimeSpan.FromSeconds(6));
        service.Raise("AL301", AlarmLevel.Critical, "真空吸不住", "贴附头");

        Assert.Equal(2, repository.Query(clock.Now.AddMinutes(-10), clock.Now.AddMinutes(10)).Count);
    }

    [Fact]
    public void Ack_RemovesFromActiveAndKeepsHistory()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var clock = new FakeClock(new DateTime(2026, 11, 8, 10, 0, 0));
        var repository = database.CreateAlarmRepository();
        var service = new AlarmService(repository, clock, NullLogger<AlarmService>.Instance);

        service.Raise("AL301", AlarmLevel.Critical, "真空吸不住", "贴附头");
        var active = Assert.Single(service.GetActiveAlarms());

        service.Ack(active.Id, "tech01");

        Assert.Empty(service.GetActiveAlarms());
        var history = repository.Query(clock.Now.AddMinutes(-10), clock.Now.AddMinutes(10));
        var record = Assert.Single(history);
        Assert.True(record.IsAcked);
        Assert.Equal("tech01", record.AckUser);
        Assert.NotNull(record.AckTime);

        // 已确认的严重报警不再阻塞启动（互锁第 10 条）
        Assert.False(service.HasUnackedCritical());
    }

    [Fact]
    public void AckAll_ConfirmsEveryActiveAlarm()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var clock = new FakeClock(new DateTime(2026, 11, 8, 10, 0, 0));
        var repository = database.CreateAlarmRepository();
        var service = new AlarmService(repository, clock, NullLogger<AlarmService>.Instance);

        service.Raise("AL301", AlarmLevel.Critical, "真空吸不住", "贴附头");
        service.Raise("AL202", AlarmLevel.Warning, "MARK 识别失败", "视觉");

        Assert.Equal(2, service.AckAll("admin"));
        Assert.Empty(service.GetActiveAlarms());
    }

    [Fact]
    public void Recover_WritesEndTimeAndClearsActiveList()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var clock = new FakeClock(new DateTime(2026, 11, 8, 10, 0, 0));
        var repository = database.CreateAlarmRepository();
        var service = new AlarmService(repository, clock, NullLogger<AlarmService>.Instance);

        service.Raise("AL401", AlarmLevel.Critical, "安全门被打开", "安全");
        clock.Advance(TimeSpan.FromSeconds(3));

        service.Recover("AL401");

        Assert.Empty(service.GetActiveAlarms());
        var record = Assert.Single(repository.Query(clock.Now.AddMinutes(-10), clock.Now.AddMinutes(10)));
        Assert.NotNull(record.EndTime);
        Assert.False(record.IsAcked);   // 自动恢复不等于人工确认
    }

    [Fact]
    public void Recover_IgnoresNonRecoverableCodes()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var clock = new FakeClock(new DateTime(2026, 11, 8, 10, 0, 0));
        var repository = database.CreateAlarmRepository();
        var service = new AlarmService(repository, clock, NullLogger<AlarmService>.Instance);

        // 09 文档第三节：AL100/AL101/AL103/AL105 必须人工处理
        service.Raise("AL100", AlarmLevel.Critical, "急停被按下", "安全");
        service.Recover("AL100");

        Assert.Single(service.GetActiveAlarms());
    }

    [Fact]
    public void Recover_WithoutActiveAlarm_IsNoOp()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var clock = new FakeClock(new DateTime(2026, 11, 8, 10, 0, 0));
        var service = new AlarmService(database.CreateAlarmRepository(), clock, NullLogger<AlarmService>.Instance);

        service.Recover("AL201");

        Assert.Empty(service.GetActiveAlarms());
    }

    /// <summary>可手动推进的测试时钟（去重窗口 / 恢复时间都依赖它）。</summary>
    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTime now) => Now = now;

        public DateTime Now { get; private set; }

        public DateTime UtcNow => Now.ToUniversalTime();

        public void Advance(TimeSpan delta) => Now = Now.Add(delta);
    }
}
