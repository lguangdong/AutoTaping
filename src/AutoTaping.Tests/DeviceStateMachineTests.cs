// 职责：DeviceStateMachine 单元测试（全部合法迁移 + 关键非法/守卫拒绝场景），对应 S6 验收标准第 9 条
// 层：Tests
// 依赖：AutoTaping.Core.StateMachine

using AutoTaping.Core.StateMachine;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTaping.Tests;

/// <summary>
/// 状态机迁移测试。覆盖 05 文档第 2 节迁移表：合法迁移、守卫拒绝、模式切换、非法迁移状态不变。
/// </summary>
public sealed class DeviceStateMachineTests
{
    [Fact]
    public void InitializingToReady_WhenSelfCheckPasses()
    {
        var machine = NewMachine();
        Assert.True(machine.TryTransition(DeviceState.Ready));
        Assert.Equal(DeviceState.Ready, machine.State);
    }

    [Fact]
    public void InitializingToNotReady_WhenSelfCheckFails()
    {
        var machine = NewMachine();
        Assert.True(machine.TryTransition(DeviceState.NotReady));
        Assert.Equal(DeviceState.NotReady, machine.State);
    }

    [Fact]
    public void InitializingToAlarming_WhenSevereFault()
    {
        var machine = NewMachine();
        Assert.True(machine.TryTransition(DeviceState.Alarming));
        Assert.Equal(DeviceState.Alarming, machine.State);
    }

    [Fact]
    public void NotReadyToReady_WhenDoorClosedAndMotionConnected()
    {
        var machine = NewMachine();
        Assert.True(machine.TryTransition(DeviceState.NotReady));
        Assert.True(machine.TryTransition(DeviceState.Ready));
    }

    [Fact]
    public void NotReadyToReady_RejectedWhenGuardFails()
    {
        var machine = NewMachine();
        Assert.True(machine.TryTransition(DeviceState.NotReady));
        machine.RegisterGuard(DeviceState.NotReady, DeviceState.Ready, () => false);
        Assert.False(machine.TryTransition(DeviceState.Ready));
        Assert.Equal(DeviceState.NotReady, machine.State);
    }

    [Fact]
    public void ReadyToRunning_WhenGuardPasses()
    {
        var machine = NewMachine();
        Assert.True(machine.TryTransition(DeviceState.Ready));
        Assert.True(machine.TryTransition(DeviceState.Running));
    }

    [Fact]
    public void ReadyToRunning_RejectedWhenNoRecipe()
    {
        var machine = NewMachine();
        Assert.True(machine.TryTransition(DeviceState.Ready));
        machine.RegisterGuard(DeviceState.Ready, DeviceState.Running, () => false);
        Assert.False(machine.TryTransition(DeviceState.Running));
        Assert.Equal(DeviceState.Ready, machine.State);
    }

    [Fact]
    public void RunningToPaused_AndPausedToRunning()
    {
        var machine = NewMachine();
        Assert.True(machine.TryTransition(DeviceState.Ready));
        Assert.True(machine.TryTransition(DeviceState.Running));
        Assert.True(machine.TryTransition(DeviceState.Paused));
        Assert.True(machine.TryTransition(DeviceState.Running));
    }

    [Fact]
    public void PausedToRunning_RejectedWhenGuardFails()
    {
        var machine = NewMachine();
        Assert.True(machine.TryTransition(DeviceState.Ready));
        Assert.True(machine.TryTransition(DeviceState.Running));
        Assert.True(machine.TryTransition(DeviceState.Paused));
        machine.RegisterGuard(DeviceState.Paused, DeviceState.Running, () => false);
        Assert.False(machine.TryTransition(DeviceState.Running));
        Assert.Equal(DeviceState.Paused, machine.State);
    }

    [Fact]
    public void RunningToReady_OnStop()
    {
        var machine = NewMachine();
        Assert.True(machine.TryTransition(DeviceState.Ready));
        Assert.True(machine.TryTransition(DeviceState.Running));
        Assert.True(machine.TryTransition(DeviceState.Ready));
    }

    [Fact]
    public void RunningToAlarming_OnFlowFailure()
    {
        var machine = NewMachine();
        Assert.True(machine.TryTransition(DeviceState.Ready));
        Assert.True(machine.TryTransition(DeviceState.Running));
        Assert.True(machine.TryTransition(DeviceState.Alarming));
    }

    [Fact]
    public void AlarmingToReady_WhenNoUnackedCritical()
    {
        var machine = NewMachine();
        Assert.True(machine.TryTransition(DeviceState.Alarming));
        Assert.True(machine.TryTransition(DeviceState.Ready));
    }

    [Fact]
    public void AlarmingToReady_RejectedWhenUnackedCritical()
    {
        var machine = NewMachine();
        Assert.True(machine.TryTransition(DeviceState.Alarming));
        machine.RegisterGuard(DeviceState.Alarming, DeviceState.Ready, () => false);
        Assert.False(machine.TryTransition(DeviceState.Ready));
        Assert.Equal(DeviceState.Alarming, machine.State);
    }

    [Fact]
    public void AnyStateToEStopped()
    {
        var machine = NewMachine();
        Assert.True(machine.TryTransition(DeviceState.Ready));
        Assert.True(machine.TryTransition(DeviceState.Running));
        Assert.True(machine.TryTransition(DeviceState.EStopped));
        Assert.Equal(DeviceState.EStopped, machine.State);
    }

    [Fact]
    public void EStoppedToNotReady_WhenReleased()
    {
        var machine = NewMachine();
        Assert.True(machine.TryTransition(DeviceState.EStopped));
        Assert.True(machine.TryTransition(DeviceState.NotReady));
    }

    [Fact]
    public void EStoppedToNotReady_RejectedWhenNotReleased()
    {
        var machine = NewMachine();
        Assert.True(machine.TryTransition(DeviceState.EStopped));
        machine.RegisterGuard(DeviceState.EStopped, DeviceState.NotReady, () => false);
        Assert.False(machine.TryTransition(DeviceState.NotReady));
        Assert.Equal(DeviceState.EStopped, machine.State);
    }

    [Fact]
    public void Running_SwitchToManual_Rejected()
    {
        var machine = NewMachine();
        Assert.True(machine.TryTransition(DeviceState.Ready));
        Assert.True(machine.TryTransition(DeviceState.Running));
        Assert.False(machine.TrySwitchMode(OperationMode.Manual));
        Assert.Equal(OperationMode.Auto, machine.Mode);
    }

    [Fact]
    public void Ready_SwitchMode_Succeeds()
    {
        var machine = NewMachine();
        Assert.True(machine.TryTransition(DeviceState.Ready));
        Assert.True(machine.TrySwitchMode(OperationMode.Manual));
        Assert.Equal(OperationMode.Manual, machine.Mode);
    }

    [Fact]
    public void InvalidTransition_ReturnsFalse_AndStateUnchanged()
    {
        var machine = NewMachine();
        Assert.True(machine.TryTransition(DeviceState.Ready));

        // Ready 下只有 Running / NotReady / EStopped 合法，其余（Paused / Alarming）非法且状态不变
        Assert.False(machine.TryTransition(DeviceState.Paused));
        Assert.False(machine.TryTransition(DeviceState.Alarming));
        Assert.Equal(DeviceState.Ready, machine.State);
    }

    [Fact]
    public void StateChanged_RaisedOnSuccessfulTransition()
    {
        var machine = NewMachine();
        StateChangedEventArgs? args = null;
        machine.StateChanged += (_, e) => args = e;

        Assert.True(machine.TryTransition(DeviceState.NotReady));
        Assert.NotNull(args);
        Assert.Equal(DeviceState.Initializing, args!.From);
        Assert.Equal(DeviceState.NotReady, args.To);
    }

    /// <summary>创建带全放行守卫的状态机（具体测试再覆盖某个守卫为 false）。</summary>
    private static DeviceStateMachine NewMachine()
    {
        var machine = new DeviceStateMachine(NullLogger<DeviceStateMachine>.Instance);
        machine.RegisterGuard(DeviceState.Initializing, DeviceState.Ready, () => true);
        machine.RegisterGuard(DeviceState.Initializing, DeviceState.NotReady, () => true);
        machine.RegisterGuard(DeviceState.NotReady, DeviceState.Ready, () => true);
        machine.RegisterGuard(DeviceState.Ready, DeviceState.Running, () => true);
        machine.RegisterGuard(DeviceState.Paused, DeviceState.Running, () => true);
        machine.RegisterGuard(DeviceState.Alarming, DeviceState.Ready, () => true);
        machine.RegisterGuard(DeviceState.EStopped, DeviceState.NotReady, () => true);
        machine.RegisterGuard(DeviceState.Ready, DeviceState.NotReady, () => true);
        return machine;
    }
}
