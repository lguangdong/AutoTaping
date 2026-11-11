// 职责：雷赛运控卡初始化所需的硬件参数模型（脉冲当量/软限位/回零/速度曲线），由 App 从 appsettings.json 填充
// 层：Hardware/Motion
// 依赖：无

namespace AutoTaping.Hardware.Motion;

/// <summary>
/// 单个轴的雷赛卡硬件参数。单位换算（脉冲 → mm / deg）由 <c>dmc_set_equiv</c> 完成，
/// 业务层不得做脉冲换算（02 文档 4.1 节）。所有值均来自 <c>appsettings.json</c> 的
/// <c>Hardware:MotionCard:Axes</c>（10-编码约定 第八节：硬件参数进配置）。
/// </summary>
public sealed class LeadshineAxisOptions
{
    /// <summary>脉冲当量：X/Y/Z 为 pulse/mm，R 轴为 pulse/deg。</summary>
    public double PulsePerUnit { get; init; }

    /// <summary>速度曲线最小速度（mm/s 或 deg/s）。</summary>
    public double MinSpeed { get; init; }

    /// <summary>速度曲线最大速度（mm/s 或 deg/s），动态调速以它为上限。</summary>
    public double MaxSpeed { get; init; }

    /// <summary>加速时间（ms）。</summary>
    public double AccTimeMs { get; init; }

    /// <summary>减速时间（ms）。</summary>
    public double DecTimeMs { get; init; }

    /// <summary>软限位下限（mm 或 deg）。</summary>
    public double MinLimit { get; init; }

    /// <summary>软限位上限（mm 或 deg）。</summary>
    public double MaxLimit { get; init; }

    /// <summary>回零模式（取值以雷赛手册为准）。</summary>
    public ushort HomeMode { get; init; }

    /// <summary>回零速度（mm/s 或 deg/s）。</summary>
    public double HomeSpeed { get; init; }

    /// <summary>回零加速度（mm/s² 或 deg/s²）。</summary>
    public double HomeAcc { get; init; }
}

/// <summary>
/// 雷赛运控卡的整卡参数（卡号 + 4 轴参数，下标即 <see cref="AutoTaping.Core.Abstractions.AxisId"/>）。
/// </summary>
public sealed class LeadshineMotionCardOptions
{
    /// <summary>板卡号（默认 0，单卡场景）。</summary>
    public ushort CardNo { get; init; }

    /// <summary>4 轴参数，下标即 AxisId（长度固定 4）。</summary>
    public LeadshineAxisOptions[] Axes { get; init; } = Array.Empty<LeadshineAxisOptions>();
}
