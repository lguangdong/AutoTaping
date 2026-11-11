// 职责：操作日志实体（对应 SQLite OperationLog 表），用于参数修改、启动、标定等操作审计
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 操作日志。哪些操作必须记录见 <c>docs/10-编码约定与AI须知.md</c> 7.3 节。
/// </summary>
public sealed class OperationLog
{
    /// <summary>主键，新增时由数据库生成。</summary>
    public int Id { get; set; }

    /// <summary>操作用户名。</summary>
    public string UserName { get; set; } = "";

    /// <summary>操作动作，如 <c>修改参数</c> / <c>启动</c> / <c>标定</c> / <c>登录</c>。</summary>
    public string Action { get; set; } = "";

    /// <summary>操作对象，如 <c>示例配方.process.holdPressureMs</c>。</summary>
    public string Target { get; set; } = "";

    /// <summary>变更前值；无变更语义时为 <see langword="null"/>。</summary>
    public string? OldValue { get; set; }

    /// <summary>变更后值；无变更语义时为 <see langword="null"/>。</summary>
    public string? NewValue { get; set; }

    /// <summary>操作发生时间。</summary>
    public DateTime CreatedAt { get; set; }
}
