// 职责：IO 点位映射（点位号 → 中文名），供手动页与 IO 页的点位网格显示
// 层：App/Infrastructure
// 依赖：无（由 App 启动时从 appsettings.json 的 Io:DiMap / Io:DoMap 填充后注册为单例）

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// IO 点位映射。键为点位号（<c>X00</c> / <c>Y00</c>），值为中文名称，
/// 两者都来自 <c>appsettings.json</c>（08-页面清单 页面 7：中文名来自 DiMap / DoMap）。
/// </summary>
public sealed class IoPointMap
{
    /// <summary>输入点位（按配置文件顺序，即现场电气图顺序）。</summary>
    public IReadOnlyList<IoPointDefinition> Di { get; init; } = Array.Empty<IoPointDefinition>();

    /// <summary>输出点位（按配置文件顺序）。</summary>
    public IReadOnlyList<IoPointDefinition> Do { get; init; } = Array.Empty<IoPointDefinition>();
}

/// <summary>单个 IO 点位的展示定义。</summary>
/// <param name="Point">点位号，如 <c>"X00"</c>。</param>
/// <param name="Name">中文名称，如 <c>"上料位有料"</c>。</param>
public sealed record IoPointDefinition(string Point, string Name);
