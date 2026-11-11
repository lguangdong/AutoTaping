// 职责：采集线程每 200ms 产出的设备状态快照（轴位置/回零/到位/停止原因 + DI/DO 状态），界面只读消费
// 层：Core/Models
// 依赖：AxisId（Core/Abstractions）；无 UI、无硬件、无 IO

using AutoTaping.Core.Abstractions;

namespace AutoTaping.Core.Models;

/// <summary>
/// 设备状态快照（01-架构设计 第 3 节的采集线程模型）。由采集线程整体替换、界面线程只读，
/// 因此内部数组在创建后不再修改：读取方不需要加锁，也不会读到半新半旧的状态。
/// </summary>
public sealed class DeviceSnapshot
{
    /// <summary>尚未采集到任何数据时使用的空快照（全部为 0 / false）。</summary>
    public static readonly DeviceSnapshot Empty = new();

    /// <summary>4 轴当前位置，下标即 <see cref="AxisId"/>（mm，R 轴为 °）。</summary>
    public double[] AxisPositions { get; init; } = new double[4];

    /// <summary>4 轴是否到位（已停止且无异常），下标即 <see cref="AxisId"/>。</summary>
    public bool[] AxisInPosition { get; init; } = new bool[4];

    /// <summary>4 轴是否已回零，下标即 <see cref="AxisId"/>。</summary>
    public bool[] AxisHomed { get; init; } = new bool[4];

    /// <summary>4 轴停止原因，下标即 <see cref="AxisId"/>（取值见 <c>MotionErrorCodes</c>）。</summary>
    public int[] AxisStopReasons { get; init; } = new int[4];

    /// <summary>运控卡是否已连接；未连接时一切运动控制必须禁用（05-核心状态机 互锁第 5 条）。</summary>
    public bool MotionConnected { get; init; }

    /// <summary>相机是否已连接；未连接时禁止自动运行（05-核心状态机 互锁第 4 条）。</summary>
    public bool CameraConnected { get; init; }

    /// <summary>全部输入点位状态（点位名 → ON/OFF）。</summary>
    public IReadOnlyDictionary<string, bool> DiStates { get; init; } = new Dictionary<string, bool>();

    /// <summary>全部输出点位状态（含强制输出的当前生效值）。</summary>
    public IReadOnlyDictionary<string, bool> DoStates { get; init; } = new Dictionary<string, bool>();

    /// <summary>当前处于强制输出状态的输出点位名集合（IO 页需高亮标注「强制」）。</summary>
    public IReadOnlyCollection<string> ForcedPoints { get; init; } = Array.Empty<string>();

    /// <summary>本帧采集时刻。</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>读取指定轴位置。</summary>
    /// <param name="axis">轴标识。</param>
    public double GetPosition(AxisId axis) => AxisPositions[(int)axis];

    /// <summary>指定轴是否到位。</summary>
    /// <param name="axis">轴标识。</param>
    public bool IsInPosition(AxisId axis) => AxisInPosition[(int)axis];

    /// <summary>指定轴是否已回零。</summary>
    /// <param name="axis">轴标识。</param>
    public bool IsHomed(AxisId axis) => AxisHomed[(int)axis];

    /// <summary>读取指定轴停止原因。</summary>
    /// <param name="axis">轴标识。</param>
    public int GetStopReason(AxisId axis) => AxisStopReasons[(int)axis];

    /// <summary>读取输入点位；点位不存在时按 OFF 处理。</summary>
    /// <param name="point">点位名，如 <c>"X05"</c>。</param>
    public bool Di(string point) => DiStates.TryGetValue(point, out var value) && value;

    /// <summary>读取输出点位；点位不存在时按 OFF 处理。</summary>
    /// <param name="point">点位名，如 <c>"Y00"</c>。</param>
    public bool Do(string point) => DoStates.TryGetValue(point, out var value) && value;

    /// <summary>指定输出点位是否处于强制输出状态。</summary>
    /// <param name="point">点位名，如 <c>"Y00"</c>。</param>
    public bool IsForced(string point) => ForcedPoints.Contains(point);
}
