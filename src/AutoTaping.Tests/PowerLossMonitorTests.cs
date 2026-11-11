// 职责：断电检测测试（首次启动、正常退出、非正常退出三种路径）
// 层：Tests
// 依赖：TestDatabase、PowerLossMonitor

using AutoTaping.Core.Services;
using AutoTaping.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTaping.Tests;

/// <summary>
/// <see cref="PowerLossMonitor"/> 的测试，对应 S1 交付物「断电检测（SystemState 读写）」。
/// </summary>
public sealed class PowerLossMonitorTests
{
    [Fact]
    public void DetectAndMarkRunning_ReportsUncleanShutdown_OnlyAfterRunningState()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var stateRepository = database.CreateSystemStateRepository();

        // 首次启动：没有该键，不应误报断电
        var firstRun = CreateMonitor(database);
        Assert.False(firstRun.DetectAndMarkRunning());
        Assert.Equal("0", stateRepository.Get(PowerLossMonitor.LastShutdownCleanKey));

        // 正常退出：写 1
        firstRun.MarkCleanShutdown();
        Assert.Equal("1", stateRepository.Get(PowerLossMonitor.LastShutdownCleanKey));

        var afterCleanExit = CreateMonitor(database);
        Assert.False(afterCleanExit.DetectAndMarkRunning());

        // 运行中被拔电源：键保持 "0"，下次启动判定为非正常退出
        var afterPowerLoss = CreateMonitor(database);
        Assert.True(afterPowerLoss.DetectAndMarkRunning());

        // 未再发生正常退出前，每次启动都会读到 "0"（06 文档第 8 节的约定），因此仍判为上次非正常退出；
        // 该重复提示由 S7 报警服务的去重规则收敛
        var again = CreateMonitor(database);
        Assert.True(again.DetectAndMarkRunning());

        // 一旦正常退出过，下一次启动即恢复正常
        again.MarkCleanShutdown();
        Assert.False(CreateMonitor(database).DetectAndMarkRunning());
    }

    private static PowerLossMonitor CreateMonitor(TestDatabase database)
        => new(database.CreateSystemStateRepository(),
               NullLogger<PowerLossMonitor>.Instance);
}
