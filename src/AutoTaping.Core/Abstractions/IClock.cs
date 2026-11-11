// 职责：定义"取当前时间"的抽象，避免业务代码直接调用 DateTime.Now（便于单元测试注入固定时间）
// 层：Core/Abstractions
// 依赖：无

namespace AutoTaping.Core.Abstractions;

/// <summary>
/// 时钟抽象。所有需要当前时间的地方（落库时间戳、节拍计算、超时判定）都通过本接口取时间。
/// </summary>
public interface IClock
{
    /// <summary>当前本地时间。</summary>
    DateTime Now { get; }

    /// <summary>当前 UTC 时间。</summary>
    DateTime UtcNow { get; }
}
