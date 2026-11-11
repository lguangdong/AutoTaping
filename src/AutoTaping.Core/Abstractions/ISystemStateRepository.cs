// 职责：系统状态键值（SystemState 表）读写抽象，用于断电检测等跨启动状态记忆
// 层：Core/Abstractions
// 依赖：无

namespace AutoTaping.Core.Abstractions;

/// <summary>
/// 系统状态键值仓储，对应 <c>SystemState</c> 表（Key / Value / UpdatedAt）。
/// 关键约定：<c>LastShutdownClean</c> 正常退出时写 <c>"1"</c>，启动时读取并立即置 <c>"0"</c>
/// （见 <c>docs/06-数据模型与数据库.md</c> 第 8 节）。
/// </summary>
public interface ISystemStateRepository
{
    /// <summary>读取键值；不存在返回 <see langword="null"/>。</summary>
    /// <param name="key">状态键，如 <c>LastShutdownClean</c>。</param>
    string? Get(string key);

    /// <summary>写入键值（存在则更新，不存在则插入）。</summary>
    /// <param name="key">状态键。</param>
    /// <param name="value">状态值，不得为 <see langword="null"/>。</param>
    void Set(string key, string value);
}
