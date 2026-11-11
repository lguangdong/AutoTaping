// 职责：登录 / 登出 / 当前用户 / 权限判定的抽象（界面导航与 ViewModel 二次校验的唯一入口）
// 层：Core/Abstractions
// 依赖：User、AuthResult、Permission（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.Core.Abstractions;

/// <summary>
/// 认证与权限服务。界面用 <see cref="CurrentUser"/> 显示当前用户、用 <see cref="HasPermission"/>
/// 决定导航是否可用；ViewModel 在执行受限操作前必须再调用一次（00-项目总览 第 5 节：
/// 权限不只在界面隐藏，ViewModel 层必须再校验一次）。
/// </summary>
public interface IAuthService
{
    /// <summary>当前登录用户；未登录为 <see langword="null"/>。</summary>
    User? CurrentUser { get; }

    /// <summary>是否已登录。</summary>
    bool IsAuthenticated { get; }

    /// <summary>本次运行中最后一次登录成功的用户名（用于登录框预填，未登录过为 <see langword="null"/>）。</summary>
    string? LastUserName { get; }

    /// <summary>当前用户发生变化（登录 / 登出 / 自动锁屏）时触发。事件在调用方线程上同步触发。</summary>
    event EventHandler? CurrentUserChanged;

    /// <summary>校验用户名与口令并登录。</summary>
    /// <param name="userName">登录名。</param>
    /// <param name="password">明文口令。</param>
    /// <returns>登录结果；失败时 <see cref="AuthResult.Message"/> 为面向操作工的原因。</returns>
    AuthResult Login(string userName, string password);

    /// <summary>登出（自动锁屏也走这里，保证日志与界面状态一致）。未登录时安全无操作。</summary>
    void Logout();

    /// <summary>当前用户是否具备指定权限；未登录一律返回 <see langword="false"/>。</summary>
    /// <param name="permission">权限点。</param>
    bool HasPermission(Permission permission);
}
