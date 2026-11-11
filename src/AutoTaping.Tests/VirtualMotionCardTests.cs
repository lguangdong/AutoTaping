// 职责：VirtualMotionCard 单元测试（位置推进 / 软限位拒绝 / 急停拒绝），对应 S2 验收标准第 6 条
// 层：Tests
// 依赖：AutoTaping.Hardware.Motion、AutoTaping.Core.Abstractions、AutoTaping.Core.Exceptions

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Exceptions;
using AutoTaping.Hardware.Motion;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTaping.Tests;

/// <summary>
/// <see cref="VirtualMotionCard"/> 的行为测试：位置按时间线性推进、超软限位触发 <see cref="AxisFault"/> 并拒绝、
/// 急停后拒绝一切运动直到复位。
/// </summary>
public sealed class VirtualMotionCardTests
{
    [Fact]
    public async Task VirtualMotionCard_AdvancesPositionToTarget()
    {
        using var card = CreateCard();
        Assert.True(card.Initialize());
        Assert.True(card.IsConnected);

        card.MoveAbsolute(AxisId.X, 100, 100);

        // 100mm / 100mm/s ≈ 1s，留余量轮询到位
        var deadline = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < deadline && !card.IsInPosition(AxisId.X))
        {
            await Task.Delay(20);
        }

        Assert.Equal(100, card.GetPosition(AxisId.X), 1);
        Assert.True(card.IsInPosition(AxisId.X));
        Assert.Equal(MotionErrorCodes.Normal, card.GetStopReason(AxisId.X));
    }

    [Fact]
    public void VirtualMotionCard_RejectsMoveBeyondSoftLimit()
    {
        using var card = CreateCard();
        card.Initialize();

        AxisFaultEventArgs? fault = null;
        card.AxisFault += (_, e) => fault = e;

        // 默认 X 软限位 0~400，目标 500 超出
        var ex = Assert.Throws<HardwareException>(() => card.MoveAbsolute(AxisId.X, 500, 100));

        Assert.NotNull(fault);
        Assert.Equal(AxisId.X, fault!.Axis);
        Assert.Equal(MotionErrorCodes.SoftLimit, fault.Reason);
        Assert.Contains("软限位", fault.Message);
        Assert.Equal(0, card.GetPosition(AxisId.X), 1);   // 位置未变
    }

    [Fact]
    public void VirtualMotionCard_RejectsMoveAfterEmergencyStop()
    {
        using var card = CreateCard();
        card.Initialize();

        card.EmergencyStopAll();

        // 急停后运动被拒绝
        var ex = Assert.Throws<HardwareException>(() => card.MoveAbsolute(AxisId.X, 100, 100));
        Assert.Equal(MotionErrorCodes.EmergencyStop, ex.ErrorCode);

        // 复位后可再动
        card.ResetAxis(AxisId.X);
        card.MoveAbsolute(AxisId.X, 100, 100);
    }

    private static VirtualMotionCard CreateCard() => new(NullLogger<VirtualMotionCard>.Instance);
}
