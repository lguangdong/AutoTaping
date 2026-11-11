// 职责：InterlockChecker 单元测试（10 条互锁逐条验证拒绝场景），对应 S6 验收标准第 9 条
// 层：Tests
// 依赖：AutoTaping.Core.Services、AutoTaping.Core.Models

using AutoTaping.Core.Models;
using AutoTaping.Core.Services;

namespace AutoTaping.Tests;

/// <summary>
/// 互锁检查器测试。10 条互锁与 <c>docs/05-核心状态机与流程.md</c> 第 6 节一一对应，
/// 每条互锁验证「不满足时返回失败原因，满足时返回 null」。
/// </summary>
public sealed class InterlockCheckerTests
{
    private readonly InterlockChecker _checker = new();

    [Fact]
    public void Interlock1_AxesHomed()
    {
        Assert.Null(_checker.CheckAxesHomed(CreateSnapshot(allHomed: true)));
        Assert.NotNull(_checker.CheckAxesHomed(CreateSnapshot(allHomed: false)));
    }

    [Fact]
    public void Interlock2_SafetyDoorClosed()
    {
        Assert.Null(_checker.CheckSafetyDoorClosed(CreateSnapshot(x05: true)));
        Assert.NotNull(_checker.CheckSafetyDoorClosed(CreateSnapshot(x05: false)));
    }

    [Fact]
    public void Interlock3_AirPressure()
    {
        Assert.Null(_checker.CheckAirPressure(CreateSnapshot(x07: true)));
        Assert.NotNull(_checker.CheckAirPressure(CreateSnapshot(x07: false)));
    }

    [Fact]
    public void Interlock4_CameraReady()
    {
        Assert.Null(_checker.CheckCameraReady(CreateSnapshot(cameraConnected: true)));
        Assert.NotNull(_checker.CheckCameraReady(CreateSnapshot(cameraConnected: false)));
    }

    [Fact]
    public void Interlock5_MotionConnected()
    {
        Assert.Null(_checker.CheckMotionConnected(CreateSnapshot(motionConnected: true)));
        Assert.NotNull(_checker.CheckMotionConnected(CreateSnapshot(motionConnected: false)));
    }

    [Fact]
    public void Interlock6_RecipeActive()
    {
        Assert.Null(_checker.CheckRecipeActive(new Recipe()));
        Assert.NotNull(_checker.CheckRecipeActive(null));
    }

    [Fact]
    public void Interlock7_EmergencyStopReleased()
    {
        Assert.Null(_checker.CheckEmergencyStopReleased(CreateSnapshot(x06: true)));
        Assert.NotNull(_checker.CheckEmergencyStopReleased(CreateSnapshot(x06: false)));
    }

    [Fact]
    public void Interlock8_ZSafeHeight()
    {
        Assert.Null(_checker.CheckZSafeHeight(CreateSnapshot(zPos: 50.0), 50.0));
        Assert.NotNull(_checker.CheckZSafeHeight(CreateSnapshot(zPos: 2.0), 50.0));
    }

    [Fact]
    public void Interlock9_NoAxisMoving()
    {
        Assert.Null(_checker.CheckNoAxisMoving(CreateSnapshot(allInPosition: true)));
        Assert.NotNull(_checker.CheckNoAxisMoving(CreateSnapshot(allInPosition: false)));
    }

    [Fact]
    public void Interlock10_NoUnackedCriticalAlarm()
    {
        Assert.Null(_checker.CheckNoUnackedCriticalAlarm(hasUnackedCritical: false));
        Assert.NotNull(_checker.CheckNoUnackedCriticalAlarm(hasUnackedCritical: true));
    }

    [Fact]
    public void CheckForStart_AggregatesAllFailures()
    {
        var snap = CreateSnapshot(allHomed: false, x05: false, cameraConnected: false, motionConnected: false);
        var errors = _checker.CheckForStart(snap, recipe: null, hasUnackedCritical: true);

        Assert.Contains("4 轴未全部回零", errors);
        Assert.Contains("安全门未闭合", errors);
        Assert.Contains("相机未就绪", errors);
        Assert.Contains("运控卡未连接", errors);
        Assert.Contains("未选择配方", errors);
        Assert.Contains("存在未确认的严重报警", errors);
    }

    /// <summary>构造一帧快照，默认全部满足互锁，可按需破坏某个条件。</summary>
    private static DeviceSnapshot CreateSnapshot(
        bool allHomed = true,
        bool allInPosition = true,
        bool x05 = true,
        bool x06 = true,
        bool x07 = true,
        bool motionConnected = true,
        bool cameraConnected = true,
        double zPos = 50.0)
    {
        return new DeviceSnapshot
        {
            AxisPositions = new[] { 0.0, 0.0, zPos, 0.0 },
            AxisInPosition = new[] { allInPosition, allInPosition, allInPosition, allInPosition },
            AxisHomed = new[] { allHomed, allHomed, allHomed, allHomed },
            AxisStopReasons = new[] { 0, 0, 0, 0 },
            MotionConnected = motionConnected,
            CameraConnected = cameraConnected,
            DiStates = new Dictionary<string, bool>
            {
                ["X05"] = x05,
                ["X06"] = x06,
                ["X07"] = x07
            },
            DoStates = new Dictionary<string, bool>(),
            ForcedPoints = Array.Empty<string>(),
            Timestamp = DateTime.Now
        };
    }
}
