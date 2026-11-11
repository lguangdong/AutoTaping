// 职责：用户仓储抽象（登录取用户 + S9 用户管理的增删改、改密、禁用）
// 层：Core/Abstractions
// 依赖：User（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.Core.Abstractions;

/// <summary>
/// 用户仓储。登录只用到 <see cref="GetByUserName"/> 与 <see cref="UpdateLastLogin"/>；
/// 用户管理（S9）用到其余方法，业务校验与审计由 <c>UserManagementService</c> 负责。
/// 变更记录见 <c>docs/06-数据模型与数据库.md</c> 第 5 节。
/// </summary>
public interface IUserRepository
{
    /// <summary>按登录名查询用户（含禁用账户，是否允许登录由 <see cref="IAuthService"/> 判定）。</summary>
    /// <param name="userName">登录名，大小写不敏感。</param>
    /// <returns>用户；不存在返回 <see langword="null"/>。</returns>
    User? GetByUserName(string userName);

    /// <summary>更新最后登录时间。</summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="time">登录时刻。</param>
    void UpdateLastLogin(int userId, DateTime time);

    /// <summary>取全部用户（含禁用账户），按角色倒序、用户名升序。</summary>
    IReadOnlyList<User> GetAll();

    /// <summary>按主键查询用户。</summary>
    /// <param name="userId">用户主键。</param>
    /// <returns>用户；不存在返回 <see langword="null"/>。</returns>
    User? GetById(int userId);

    /// <summary>判断登录名是否已存在（大小写不敏感）。</summary>
    /// <param name="userName">登录名。</param>
    bool ExistsUserName(string userName);

    /// <summary>新增用户，返回新主键。登录名由调用方保证唯一（表上有 UNIQUE 约束兜底）。</summary>
    /// <param name="user">待写入用户（口令只放盐与哈希）。</param>
    /// <returns>新用户主键。</returns>
    int Insert(User user);

    /// <summary>更新用户的可变字段（角色、启用状态、口令盐与哈希）。登录名不可改。</summary>
    /// <param name="user">带主键的用户对象。</param>
    void Update(User user);

    /// <summary>物理删除用户（User 表无软删除列；操作日志仍保留历史记录）。</summary>
    /// <param name="userId">用户主键。</param>
    void Delete(int userId);
}
