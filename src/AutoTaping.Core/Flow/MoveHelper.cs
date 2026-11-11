// 职责：流程移动步骤的共享辅助——MoveAndWait 失败统一转成带 AL104 的 FlowException
// 层：Core/Flow
// 依赖：IMotionCard（Core/Abstractions）、FlowException（Core/Exceptions）、AlarmLevel（Core/Models）

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Exceptions;
using AutoTaping.Core.Models;

namespace AutoTaping.Core.Flow;

/// <summary>移动步骤共享辅助（internal，仅流程步骤使用）。</summary>
internal static class MoveHelper
{
    /// <summary>移动到目标位置并等待到位；失败 / 超时抛 AL104（轴运动超时）。</summary>
    public static async Task MoveAsync(
        IMotionCard motion, AxisId axis, double position, double speed,
        int timeoutMs, string stepName, CancellationToken ct)
    {
        var ok = await motion.MoveAndWaitAsync(axis, position, speed, timeoutMs, ct);
        if (!ok)
        {
            throw new FlowException(stepName,
                                    $"{axis} 轴移动到 {position:F2} 失败或超时",
                                    "AL104", AlarmLevel.Critical, "运动控制",
                                    pauseInsteadOfAlarm: false, innerException: null);
        }
    }
}
