// 职责：登录 / 登出 / 当前用户与权限判定的实现（口令校验、最后登录时间、操作日志留痕）
// 层：Core/Services
// 依赖：IUserRepository、IOperationLogRepository、IClock、PasswordHasher、ILogger

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Core.Services;

/// <summary>
/// 认证服务实现。登录成功后把用户保留在内存中，供状态栏显示与导航权限判定使用；
/// 口令校验走 <see cref="PasswordHasher"/>（加盐 SHA256），登录 / 登出均写操作日志（10-编码约定 7.3）。
/// </summary>
public sealed class AuthService : IAuthService
{
    /// <summary>操作日志中的登录动作名。</summary>
    private const string LoginAction = "登录";

    /// <summary>操作日志中的登出动作名。</summary>
    private const string LogoutAction = "登出";

    /// <summary>用户名不存在与口令错误使用同一提示，避免暴露账号是否存在。</summary>
    private const string InvalidCredentialMessage = "用户名或密码错误";

    private readonly IUserRepository _users;
    private readonly IOperationLogRepository _operationLog;
    private readonly IClock _clock;
    private readonly ILogger<AuthService> _log;

    /// <summary>构造认证服务。</summary>
    /// <param name="users">用户仓储。</param>
    /// <param name="operationLog">操作日志仓储（记录登录 / 登出）。</param>
    /// <param name="clock">时钟（写最后登录时间）。</param>
    /// <param name="log">日志。</param>
    public AuthService(
        IUserRepository users,
        IOperationLogRepository operationLog,
        IClock clock,
        ILogger<AuthService> log)
    {
        _users = users;
        _operationLog = operationLog;
        _clock = clock;
        _log = log;
    }

    /// <inheritdoc />
    public User? CurrentUser { get; private set; }

    /// <inheritdoc />
    public bool IsAuthenticated => CurrentUser is not null;

    /// <inheritdoc />
    public string? LastUserName { get; private set; }

    /// <inheritdoc />
    public event EventHandler? CurrentUserChanged;

    /// <inheritdoc />
    public AuthResult Login(string userName, string password)
    {
        var name = userName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return AuthResult.Failure("请输入用户名");
        }

        if (string.IsNullOrEmpty(password))
        {
            return AuthResult.Failure("请输入密码");
        }

        User? user;
        try
        {
            user = _users.GetByUserName(name);
        }
        catch (Exception ex)
        {
            // 数据库不可用时不能把异常抛到界面（01-架构设计 第 5 节：永不阻断启动），但必须留痕
            _log.LogError(ex, "读取用户 {UserName} 失败，登录被拒绝", name);
            return AuthResult.Failure("无法读取用户数据，请检查 data 目录权限与磁盘空间后重试。");
        }

        if (user is null)
        {
            _log.LogWarning("登录失败：用户 {UserName} 不存在", name);
            return AuthResult.Failure(InvalidCredentialMessage);
        }

        if (!user.IsEnabled)
        {
            _log.LogWarning("登录失败：用户 {UserName} 已被禁用", name);
            return AuthResult.Failure("该账户已被禁用，请联系工程师");
        }

        if (!PasswordHasher.Verify(user.Salt, password, user.PasswordHash))
        {
            _log.LogWarning("登录失败：用户 {UserName} 口令错误", name);
            return AuthResult.Failure(InvalidCredentialMessage);
        }

        CurrentUser = user;
        LastUserName = user.UserName;

        UpdateLastLogin(user.Id);
        WriteOperationLog(user.UserName, LoginAction);

        _log.LogInformation("用户登录成功：{UserName}，角色 {Role}", user.UserName, user.Role);
        CurrentUserChanged?.Invoke(this, EventArgs.Empty);
        return AuthResult.Success();
    }

    /// <inheritdoc />
    public void Logout()
    {
        var user = CurrentUser;
        if (user is null)
        {
            return;
        }

        CurrentUser = null;
        WriteOperationLog(user.UserName, LogoutAction);

        _log.LogInformation("用户登出：{UserName}，角色 {Role}", user.UserName, user.Role);
        CurrentUserChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public bool HasPermission(Permission permission)
    {
        var user = CurrentUser;
        return user is not null && PermissionPolicy.HasPermission(user.Role, permission);
    }

    /// <summary>写最后登录时间。失败不影响登录（用户已经通过校验，不该因为统计字段写不进去而被拒）。</summary>
    private void UpdateLastLogin(int userId)
    {
        try
        {
            _users.UpdateLastLogin(userId, _clock.Now);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "更新用户 {UserId} 最后登录时间失败", userId);
        }
    }

    /// <summary>写操作日志。日志失败不影响登录，但必须留痕（否则现场无法追溯谁登录过）。</summary>
    private void WriteOperationLog(string userName, string action)
    {
        try
        {
            _operationLog.Insert(userName, action, userName, oldValue: null, newValue: null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "写操作日志失败：用户 {UserName} 动作 {Action}", userName, action);
        }
    }
}
