// 职责：AutoFlowCoordinator 集成测试——自检 → 回零 → 启动 → 虚拟流程跑通一件，验证协调器编排正确
// 层：Tests
// 依赖：AutoTaping.Core/Hardware/Vision/Data、TestDatabase

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using AutoTaping.Core.Services;
using AutoTaping.Core.StateMachine;
using AutoTaping.Data.Repositories;
using AutoTaping.Hardware.Camera;
using AutoTaping.Hardware.Io;
using AutoTaping.Hardware.Motion;
using AutoTaping.Tests.Infrastructure;
using AutoTaping.Vision;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTaping.Tests;

/// <summary>
/// 协调器端到端测试：构造后自检为未就绪 → 复位回零后为就绪 → 启动后虚拟流程产出至少一件。
/// 覆盖 S6 验收「虚拟流程完整走完一件，产量 +1」的核心路径。
/// </summary>
public sealed class AutoFlowCoordinatorTests
{
    [Fact]
    public async Task Coordinator_RunsVirtualFlowAndProducesOutput()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var clock = database.Clock;
        var motion = new VirtualMotionCard(NullLogger<VirtualMotionCard>.Instance);
        Assert.True(motion.Initialize());

        var diPoints = new[] { "X00", "X01", "X02", "X03", "X04", "X05", "X06", "X07" };
        var doPoints = new[] { "Y00", "Y01", "Y02", "Y03", "Y04", "Y05", "Y06" };
        var initialDi = new Dictionary<string, bool> { ["X00"] = true, ["X05"] = true, ["X06"] = true, ["X07"] = true };
        var io = new VirtualIoService(diPoints, doPoints, initialDi, NullLogger<VirtualIoService>.Instance);

        var camera = new VirtualCamera(NullLogger<VirtualCamera>.Instance);
        Assert.True(camera.Initialize());
        Assert.True(camera.StartGrabbing());

        using var monitor = new DeviceMonitor(motion, io, camera, clock, NullLogger<DeviceMonitor>.Instance);
        monitor.Start();

        // 等采集线程产出首帧（MotionConnected/CameraConnected 就绪）后再构造协调器，避免自检误判
        await WaitUntilAsync(() => monitor.Current.MotionConnected && monitor.Current.CameraConnected, 3000);

        var alarm = new AlarmService(database.CreateAlarmRepository(), clock, NullLogger<AlarmService>.Instance);
        var vision = new VisionService();
        var notifier = new NullNotifier();
        var outputService = new OutputService(
            database.CreateOutputRepository(),
            database.CreateDowntimeRepository(),
            database.CreateShiftProductionRepository(),
            clock,
            NullLogger<OutputService>.Instance);

        using var coordinator = new AutoFlowCoordinator(
            motion, io, camera, monitor,
            database.CreateRecipeRepository(),
            database.CreateCalibrationRepository(),
            database.CreateOutputRepository(),
            outputService,
            alarm, vision, notifier, clock,
            NullLogger<AutoFlowCoordinator>.Instance,
            NullLoggerFactory.Instance);

        // 自检完成：未回零 → 未就绪
        Assert.Equal(DeviceState.NotReady, coordinator.State);

        // 复位 → 回零 → 就绪
        coordinator.Reset();
        await WaitUntilAsync(() => coordinator.State == DeviceState.Ready, 15_000);
        Assert.Equal(DeviceState.Ready, coordinator.State);

        // 启动 → 运行 → 产出
        coordinator.Start();
        Assert.Equal(DeviceState.Running, coordinator.State);

        var outputs = database.CreateOutputRepository();
        await WaitUntilAsync(() => outputs.CountToday(OutputRecord.ResultOk) >= 1, 20_000);

        Assert.True(outputs.CountToday(OutputRecord.ResultOk) >= 1, "虚拟流程未能在超时内产出至少一件");

        // 停止
        coordinator.Stop();
        Assert.Equal(DeviceState.Ready, coordinator.State);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    /// <summary>无操作的 IUserNotifier 桩（协调器测试只关心状态与产出，不关心弹窗）。</summary>
    private sealed class NullNotifier : IUserNotifier
    {
        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message)
        {
        }

        public bool Confirm(string title, string message) => true;
    }
}
