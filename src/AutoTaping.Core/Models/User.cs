// 职责：用户实体（对应 SQLite User 表），保存加盐哈希后的口令与角色
// 层：Core/Models
// 依赖：UserRole

namespace AutoTaping.Core.Models;

/// <summary>
/// 用户。密码只以「盐 + SHA256」形式保存（见 <c>docs/06-数据模型与数据库.md</c> 第 10 节第 3 条）。
/// </summary>
public sealed class User
{
    /// <summary>主键，新增时由数据库生成。</summary>
    public int Id { get; set; }

    /// <summary>登录名，全局唯一。</summary>
    public string UserName { get; set; } = "";

    /// <summary>口令哈希（Base64），由 <c>PasswordHasher.Hash</c> 生成。</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>口令盐（Base64，16 随机字节）。</summary>
    public string Salt { get; set; } = "";

    /// <summary>用户角色，决定可见页面与可执行操作。</summary>
    public UserRole Role { get; set; } = UserRole.Operator;

    /// <summary>是否启用；禁用后不能登录。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>最后登录时间；从未登录为 <see langword="null"/>。</summary>
    public DateTime? LastLoginAt { get; set; }
}
