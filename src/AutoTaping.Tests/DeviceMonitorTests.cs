// 职责：DeviceMonitor 单元测试（快照反映轴位置、DI/DO/强制点位同步、采集失败不影响线程存活）
// 层：Tests
// 依赖：AutoTaping.Core.Services、AutoTaping.Hardware.Motion、AutoTaping.Hardware.Io

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using AutoTaping.Core.Services;
using AutoTaping.Hardware.Camera;
using AutoTaping.Hardware.Io;
using AutoTaping.Hardware.Motion;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTaping.Tests;

/// <summary>
/// <see cref="DeviceMonitor"/> 的测试：200ms 采集产出的快照必须反映运控卡与 IO 的真实状态，
/// 且单次采集异常不能终止采集线程（01-架构设计 第 6 节：采集线程异常后线程继续运行）。
/// </summary>
public sealed class DeviceMonitorTests
{
    [Fact]
    public async Task DeviceMonitor_PublishesAxisPosition()
    {
        using var motion = new VirtualMotionCard(NullLogger<VirtualMotionCard>.Instance);
        Assert.True(motion.Initialize());

        var io = CreateIo();
        var camera = CreateCamera();
        using var monitor = new DeviceMonitor(motion, io, camera, new SystemClock(), NullLogger<DeviceMonitor>.Instance);
        monitor.Start();

        // 100mm / 200mm/s ≈ 0.5s
        motion.MoveAbsolute(AxisId.X, 100, 200);

        var reached = await WaitUntilAsync(() => monitor.Current.GetPosition(AxisId.X) >= 99.0, 3000);
        Assert.True(reached, "采集快照未在超时前反映 X 轴到位位置");
        Assert.Equal(100, monitor.Current.GetPosition(AxisId.X), 1);
        Assert.True(monitor.Current.MotionConnected);
        Assert.NotEqual(default, monitor.Current.Timestamp);
    }

    [Fact]
    public async Task DeviceMonitor_CopiesDiDoStatesAndForcedPoints()
    {
        using var motion = new VirtualMotionCard(NullLogger<VirtualMotionCard>.Instance);
        motion.Initialize();

        var io = new VirtualIoService(new[] { "X00" }, new[] { "Y00", "Y01" },
                                      NullLogger<VirtualIoService>.Instance);
        var camera = CreateCamera();
        using var monitor = new DeviceMonitor(motion, io, camera, new SystemClock(), NullLogger<DeviceMonitor>.Instance);
        monitor.Start();

        io.SetDi("X00", true);
        io.WriteDo("Y00", true);
        io.ForceOutput("Y01", true);

        var reached = await WaitUntilAsync(
            () => monitor.Current.Di("X00") && monitor.Current.Do("Y00") && monitor.Current.IsForced("Y01"), 3000);

        Assert.True(reached, "采集快照未反映 DI/DO 与强制输出状态");
        Assert.True(monitor.Current.Di("X00"));
        Assert.True(monitor.Current.Do("Y00"));
        Assert.True(monitor.Current.Do("Y01"));      // 强制输出参与 DO 快照
        Assert.True(monitor.Current.IsForced("Y01"));
        Assert.False(monitor.Current.IsForced("Y00"));
    }

    [Fact]
    public async Task DeviceMonitor_KeepsRunningAfterReadFailure()
    {
        // 运控卡读取一直抛异常：采集线程只能记日志并沿用上一帧，不能崩掉进程/线程
        var motion = new ThrowingMotionCard();
        var io = CreateIo();
        var camera = CreateCamera();
        using var monitor = new DeviceMonitor(motion, io, camera, new SystemClock(), NullLogger<DeviceMonitor>.Instance);
        monitor.Start();

        await Task.Delay(600);

        Assert.Same(DeviceSnapshot.Empty, monitor.Current);

        // 线程仍然活着：停止操作正常返回
        monitor.Stop();
    }

    private static VirtualIoService CreateIo()
        => new(Array.Empty<string>(), Array.Empty<string>(), NullLogger<VirtualIoService>.Instance);

    private static VirtualCamera CreateCamera() => new(NullLogger<VirtualCamera>.Instance);

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

    /// <summary>读取必定失败的运控卡桩，用于验证采集线程的容错。</summary>
    private sealed class ThrowingMotionCard : IMotionCard
    {
        public bool Initialize() => true;

        public bool IsConnected => true;

        public double GetPosition(AxisId axis) => throw new InvalidOperationException("模拟运控卡读取失败");

        public bool IsInPosition(AxisId axis) => true;

        public int GetStopReason(AxisId axis) => MotionErrorCodes.Normal;

        public void Home(AxisId axis)
        {
        }

        public bool IsHomeCompleted(AxisId axis) => false;

        public void MoveAbsolute(AxisId axis, double position, double speed)
        {
        }

        public void MoveRelative(AxisId axis, double distance, double speed)
        {
        }

        public void JogStart(AxisId axis, int direction, double speed)
        {
        }

        public void Stop(AxisId axis, bool emergency)
        {
        }

        public void EmergencyStopAll()
        {
        }

        public void ResetAxis(AxisId axis)
        {
        }

        public void SetSoftLimit(AxisId axis, bool enable, double min, double max)
        {
        }

        public Task<bool> MoveAndWaitAsync(AxisId axis, double position, double speed, int timeoutMs,
                                           CancellationToken ct) => Task.FromResult(false);

        public event EventHandler<AxisFaultEventArgs>? AxisFault
        {
            add { }
            remove { }
        }

        public void Dispose()
        {
        }
    }
}
