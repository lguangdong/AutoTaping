// 职责：启动自检的结果值对象（磁盘剩余、数据库可写、运控卡/相机/气压状态）
// 层：Core/Models
// 依赖：无（纯数据）

namespace AutoTaping.Core.Models;

/// <summary>
/// 一次自检的结果。<see cref="DiskSpaceOk"/> 与 <see cref="DatabaseWritable"/> 由自检服务负责报警
/// （AL501 / AL502）；三项硬件状态只作展示，对应的报警由硬件初始化流程负责（AL105 / AL201 / AL402）。
/// </summary>
/// <param name="DiskSpaceOk">磁盘剩余空间是否达标（≥ 配置下限）。</param>
/// <param name="FreeDiskGb">数据目录所在分区的剩余空间（GB）；无法判断时为 -1。</param>
/// <param name="DatabaseWritable">数据库是否可写（写探针成功）。</param>
/// <param name="MotionConnected">运控卡是否连接。</param>
/// <param name="CameraConnected">相机是否连接。</param>
/// <param name="AirPressureOk">气压是否正常（DI X07）。</param>
/// <param name="CheckedAt">自检时刻。</param>
public sealed record SelfCheckReport(
    bool DiskSpaceOk,
    double FreeDiskGb,
    bool DatabaseWritable,
    bool MotionConnected,
    bool CameraConnected,
    bool AirPressureOk,
    DateTime CheckedAt)
{
    /// <summary>三项硬件条件是否全部正常。</summary>
    public bool HardwareOk => MotionConnected && CameraConnected && AirPressureOk;

    /// <summary>是否全部检查项正常。</summary>
    public bool AllOk => DiskSpaceOk && DatabaseWritable && HardwareOk;
}
