// 职责：IClock 的系统时间实现（生产环境使用；单元测试可用固定时间替代）
// 层：Core/Services
// 依赖：IClock（Core/Abstractions）

using AutoTaping.Core.Abstractions;

namespace AutoTaping.Core.Services;

/// <summary>
/// 基于系统时钟的 <see cref="IClock"/> 实现。
/// </summary>
public sealed class SystemClock : IClock
{
    /// <summary>默认单例，供不需要注入的场景（如静态初始化）使用。</summary>
    public static SystemClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTime Now => DateTime.Now;

    /// <inheritdoc />
    public DateTime UtcNow => DateTime.UtcNow;
}
