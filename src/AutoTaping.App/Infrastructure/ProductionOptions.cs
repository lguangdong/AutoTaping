// 职责：生产指标配置——班次目标产量与标准节拍（来自 appsettings 的 Production 段）
// 层：App/Infrastructure
// 依赖：无（由 App 启动时读取后注册为单例）

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// 生产指标配置。目标产量与标准节拍属于**生产计划参数**而非设备参数，因此放 appsettings
/// 的 <c>Production</c> 段而不是配方：换产品不该改变今天的计划产量。
/// </summary>
public sealed class ProductionOptions
{
    /// <summary>班次目标产量（件）；小于等于 0 表示未设目标，界面只显示实际值。</summary>
    public int ShiftTargetCount { get; init; } = 500;

    /// <summary>标准节拍（ms）；小于等于 0 表示未配置基准，界面不显示节拍对比。</summary>
    public int StandardCycleMs { get; init; }
}
