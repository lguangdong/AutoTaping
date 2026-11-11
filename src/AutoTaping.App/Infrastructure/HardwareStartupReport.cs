// 职责：启动自检降级结果的临时记录——真实硬件初始化失败时标记，供自检流程据此报警（AL105/AL201）
// 层：App/Infrastructure
// 依赖：无（由硬件注册工厂写入、InitializeHardware 读取）

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// 硬件启动自检的降级标记。真实模式（<c>Hardware:UseVirtual=false</c>）下，若运控卡 / 相机初始化失败，
/// 注册工厂会降级为 Virtual 并把对应标记置 <see langword="true"/>；<c>App.InitializeHardware</c> 据此报
/// AL105 / AL201（01-架构设计 第 5 节：自检失败永不阻断启动，只降级 + 报警）。
/// </summary>
public sealed class HardwareStartupReport
{
    /// <summary>运控卡初始化失败、已降级为 Virtual。</summary>
    public bool MotionDegraded { get; set; }

    /// <summary>相机初始化失败、已降级为 Virtual。</summary>
    public bool CameraDegraded { get; set; }
}
