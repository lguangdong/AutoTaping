// 职责：互锁检查——10 条互锁的纯函数判定 + 启动前聚合检查（05-核心状态机 第 6 节）
// 层：Core/Services
// 依赖：DeviceSnapshot、Recipe、IoPointNames（Core/Models）；无 UI、无硬件

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;

namespace AutoTaping.Core.Services;

/// <summary>
/// 互锁检查器。10 条互锁与 <c>docs/05-核心状态机与流程.md</c> 第 6 节一一对应，
/// 每条互锁一个纯函数（返回 <see langword="null"/> 表示通过，否则为面向操作工的失败原因）。
/// <see cref="CheckForStart"/> 聚合启动前的 8 条（第 1~7、10 条），第 8、9 条单独使用。
/// </summary>
public sealed class InterlockChecker
{
    /// <summary>互锁 1：4 轴已回零。</summary>
    /// <param name="snap">设备快照。</param>
    /// <param name="isHomed">
    /// 回零状态的权威查询（一般是 <c>IMotionCard.IsHomeCompleted</c>）。快照按 200ms 采样，
    /// 回零刚完成那一刻可能还是旧值；「启动」这类不可重试的操作必须读硬件真值。
    /// 传 <see langword="null"/> 时退化为读快照。
    /// </param>
    public string? CheckAxesHomed(DeviceSnapshot snap, Func<AxisId, bool>? isHomed = null)
    {
        foreach (var axis in AllAxes)
        {
            var homed = isHomed?.Invoke(axis) ?? snap.IsHomed(axis);
            if (!homed)
            {
                return "4 轴未全部回零";
            }
        }

        return null;
    }

    /// <summary>互锁 2：安全门闭合（X05=1）。</summary>
    public string? CheckSafetyDoorClosed(DeviceSnapshot snap)
        => snap.Di(IoPointNames.SafetyDoorClosed) ? null : "安全门未闭合";

    /// <summary>互锁 3：气压正常（X07=1）。</summary>
    public string? CheckAirPressure(DeviceSnapshot snap)
        => snap.Di(IoPointNames.AirPressureOk) ? null : "气压不足";

    /// <summary>互锁 4：相机就绪。</summary>
    public string? CheckCameraReady(DeviceSnapshot snap)
        => snap.CameraConnected ? null : "相机未就绪";

    /// <summary>互锁 5：运控卡连接。</summary>
    public string? CheckMotionConnected(DeviceSnapshot snap)
        => snap.MotionConnected ? null : "运控卡未连接";

    /// <summary>互锁 6：有激活配方。</summary>
    public string? CheckRecipeActive(Recipe? recipe)
        => recipe is null ? "未选择配方" : null;

    /// <summary>互锁 7：急停未触发（X06=1）。</summary>
    public string? CheckEmergencyStopReleased(DeviceSnapshot snap)
        => snap.Di(IoPointNames.EmergencyStopReleased) ? null : "急停被按下";

    /// <summary>互锁 8：Z 轴在安全高度（防 X/Y 大范围移动撞机）。</summary>
    /// <param name="snap">设备快照。</param>
    /// <param name="safeHeight">安全高度（mm），来自配方。</param>
    public string? CheckZSafeHeight(DeviceSnapshot snap, double safeHeight)
        => snap.GetPosition(AxisId.Z) >= safeHeight ? null : "Z 轴未在安全高度";

    /// <summary>互锁 9：手动模式无轴在动（禁止切回自动）。</summary>
    public string? CheckNoAxisMoving(DeviceSnapshot snap)
    {
        foreach (var axis in AllAxes)
        {
            if (!snap.IsInPosition(axis))
            {
                return $"轴 {axis} 仍在运动";
            }
        }

        return null;
    }

    /// <summary>互锁 10：无未确认的严重报警。</summary>
    /// <param name="hasUnackedCritical">是否存在未确认严重报警（由报警服务提供）。</param>
    public string? CheckNoUnackedCriticalAlarm(bool hasUnackedCritical)
        => hasUnackedCritical ? "存在未确认的严重报警" : null;

    /// <summary>
    /// 启动前聚合检查（互锁 1~7、10）。一次列全所有失败原因（05 第 6.1 节：不要让用户一个个试）。
    /// </summary>
    /// <param name="snap">设备快照。</param>
    /// <param name="recipe">当前激活配方；<see langword="null"/> 表示未选择。</param>
    /// <param name="hasUnackedCritical">是否存在未确认严重报警。</param>
    /// <param name="isHomed">回零状态权威查询；<see langword="null"/> 时读快照。</param>
    public IReadOnlyList<string> CheckForStart(DeviceSnapshot snap, Recipe? recipe, bool hasUnackedCritical,
                                               Func<AxisId, bool>? isHomed = null)
    {
        var errors = new List<string>();

        AddIfNotNull(errors, CheckAxesHomed(snap, isHomed));
        AddIfNotNull(errors, CheckSafetyDoorClosed(snap));
        AddIfNotNull(errors, CheckAirPressure(snap));
        AddIfNotNull(errors, CheckCameraReady(snap));
        AddIfNotNull(errors, CheckMotionConnected(snap));
        AddIfNotNull(errors, CheckRecipeActive(recipe));
        AddIfNotNull(errors, CheckEmergencyStopReleased(snap));
        AddIfNotNull(errors, CheckNoUnackedCriticalAlarm(hasUnackedCritical));

        return errors;
    }

    /// <summary>4 个轴的遍历顺序（与 <see cref="AxisId"/> 一致）。</summary>
    private static readonly AxisId[] AllAxes = { AxisId.X, AxisId.Y, AxisId.Z, AxisId.R };

    private static void AddIfNotNull(List<string> errors, string? error)
    {
        if (error is not null)
        {
            errors.Add(error);
        }
    }
}
