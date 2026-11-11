// 职责：用户管理的业务规则——新增 / 改角色 / 改密 / 启用禁用 / 删除，并写操作审计日志
// 层：Core/Services
// 依赖：IUserRepository、IOperationLogRepository、IClock（Core/Abstractions）、User/UserRole/UserOperationResult（Core/Models）

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Core.Services;

/// <summary>
/// 用户管理服务（08-页面清单 页面 8、00-项目总览 第 5 节）。职责：
/// <list type="bullet">
/// <item>校验登录名与口令格式、登录名唯一性。</item>
/// <item>防止把系统锁死：不允许删除 / 禁用 / 降级最后一个启用的工程师账户，也不允许删除或禁用自己。</item>
/// <item>每次操作写一条 <c>OperationLog</c> 审计记录（口令一律不落日志）。</item>
/// </list>
/// 口令只以「盐 + SHA256」形式保存（06 文档第 10 节第 3 条）。
/// </summary>
public sealed class UserManagementService
{
    /// <summary>登录名最小长度。</summary>
    private const int MinUserNameLength = 2;

    /// <summary>登录名最大长度（与界面输入框宽度匹配，避免登录时截断）。</summary>
    private const int MaxUserNameLength = 16;

    /// <summary>口令最小长度（种子账户 admin/123456 为 6 位，沿用该下限）。</summary>
    private const int MinPasswordLength = 6;

    /// <summary>口令最大长度（防止误粘贴超长文本）。</summary>
    private const int MaxPasswordLength = 20;

    /// <summary>操作日志动作名：新增用户。</summary>
    private const string ActionCreate = "新增用户";

    /// <summary>操作日志动作名：修改用户角色。</summary>
    private const string ActionChangeRole = "修改用户角色";

    /// <summary>操作日志动作名：重置口令。</summary>
    private const string ActionResetPassword = "重置口令";

    /// <summary>操作日志动作名：启用用户。</summary>
    private const string ActionEnable = "启用用户";

    /// <summary>操作日志动作名：禁用用户。</summary>
    private const string ActionDisable = "禁用用户";

    /// <summary>操作日志动作名：删除用户。</summary>
    private const string ActionDelete = "删除用户";

    /// <summary>口令类审计记录的新值（口令明文与哈希都不得进日志）。</summary>
    private const string PasswordChangedMark = "已重置";

    private readonly IUserRepository _users;
    private readonly IOperationLogRepository _operationLog;
    private readonly IClock _clock;
    private readonly ILogger<UserManagementService> _log;

    /// <summary>构造用户管理服务。</summary>
    /// <param name="users">用户仓储。</param>
    /// <param name="operationLog">操作日志仓储（审计留痕）。</param>
    /// <param name="clock">时钟（新用户创建时间）。</param>
    /// <param name="log">日志。</param>
    public UserManagementService(
        IUserRepository users,
        IOperationLogRepository operationLog,
        IClock clock,
        ILogger<UserManagementService> log)
    {
        _users = users;
        _operationLog = operationLog;
        _clock = clock;
        _log = log;
    }

    /// <summary>取全部用户（含禁用），用于用户管理列表。</summary>
    public IReadOnlyList<User> GetAll() => _users.GetAll();

    /// <summary>新增用户。</summary>
    /// <param name="userName">登录名。</param>
    /// <param name="password">明文口令（只在此处转成盐 + 哈希）。</param>
    /// <param name="role">角色。</param>
    /// <param name="operatorName">执行该操作的用户名（审计用）。</param>
    /// <returns>操作结果。</returns>
    public UserOperationResult Create(string userName, string password, UserRole role, string operatorName)
    {
        var name = userName?.Trim() ?? string.Empty;

        var nameError = ValidateUserName(name);
        if (nameError.Length > 0)
        {
            return UserOperationResult.Failure(nameError);
        }

        var passwordError = ValidatePassword(password);
        if (passwordError.Length > 0)
        {
            return UserOperationResult.Failure(passwordError);
        }

        try
        {
            if (_users.ExistsUserName(name))
            {
                return UserOperationResult.Failure($"用户名「{name}」已存在，请换一个用户名。");
            }

            var salt = PasswordHasher.CreateSalt();
            var user = new User
            {
                UserName = name,
                Salt = salt,
                PasswordHash = PasswordHasher.Hash(salt, password),
                Role = role,
                IsEnabled = true,
                CreatedAt = _clock.Now
            };

            var id = _users.Insert(user);
            user.Id = id;

            WriteAuditLog(operatorName, ActionCreate, name, oldValue: null, newValue: Describe(role));
            _log.LogInformation("新增用户 {UserName}（角色 {Role}），操作人 {Operator}", name, role, operatorName);
            return UserOperationResult.Success($"已新增用户「{name}」（{Describe(role)}）。");
        }
        catch (Exception ex)
        {
            // 数据库不可用 / 登录名并发冲突：不把异常文本抛给界面
            _log.LogError(ex, "新增用户 {UserName} 失败", name);
            return UserOperationResult.Failure("写入用户失败，请检查 data 目录权限与磁盘空间后重试。");
        }
    }

    /// <summary>修改用户角色。</summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="role">新角色。</param>
    /// <param name="operatorName">执行该操作的用户名（审计用）。</param>
    /// <returns>操作结果。</returns>
    public UserOperationResult ChangeRole(int userId, UserRole role, string operatorName)
    {
        try
        {
            var user = _users.GetById(userId);
            if (user is null)
            {
                return UserOperationResult.Failure("用户不存在，可能已被其他窗口删除，请刷新后重试。");
            }

            if (user.Role == role)
            {
                return UserOperationResult.Failure($"用户「{user.UserName}」已经是{Describe(role)}，无需修改。");
            }

            if (user.Role == UserRole.Engineer && !HasOtherEnabledEngineer(userId))
            {
                return UserOperationResult.Failure(
                    $"「{user.UserName}」是最后一个启用的工程师账户，降级后将无人能进入系统设置，请先新增或启用另一名工程师。");
            }

            var oldRole = user.Role;
            user.Role = role;
            _users.Update(user);

            WriteAuditLog(operatorName, ActionChangeRole, user.UserName, Describe(oldRole), Describe(role));
            _log.LogInformation("修改用户 {UserName} 角色：{Old} -> {New}，操作人 {Operator}",
                                user.UserName, oldRole, role, operatorName);
            return UserOperationResult.Success($"已将「{user.UserName}」的角色改为{Describe(role)}。");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "修改用户 {UserId} 角色失败", userId);
            return UserOperationResult.Failure("写入用户失败，请检查数据库后重试。");
        }
    }

    /// <summary>重置用户口令。</summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="newPassword">新明文口令。</param>
    /// <param name="operatorName">执行该操作的用户名（审计用）。</param>
    /// <returns>操作结果。</returns>
    public UserOperationResult ResetPassword(int userId, string newPassword, string operatorName)
    {
        var passwordError = ValidatePassword(newPassword);
        if (passwordError.Length > 0)
        {
            return UserOperationResult.Failure(passwordError);
        }

        try
        {
            var user = _users.GetById(userId);
            if (user is null)
            {
                return UserOperationResult.Failure("用户不存在，可能已被其他窗口删除，请刷新后重试。");
            }

            var salt = PasswordHasher.CreateSalt();
            user.Salt = salt;
            user.PasswordHash = PasswordHasher.Hash(salt, newPassword);
            _users.Update(user);

            // 审计只记「已重置」，口令明文与哈希都不得落日志（06 文档第 10 节第 3 条）
            WriteAuditLog(operatorName, ActionResetPassword, user.UserName, oldValue: null, newValue: PasswordChangedMark);
            _log.LogInformation("重置用户 {UserName} 口令，操作人 {Operator}", user.UserName, operatorName);
            return UserOperationResult.Success($"已重置「{user.UserName}」的口令，请通知本人使用新口令登录。");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "重置用户 {UserId} 口令失败", userId);
            return UserOperationResult.Failure("写入用户失败，请检查数据库后重试。");
        }
    }

    /// <summary>启用或禁用用户。</summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="enabled">是否启用。</param>
    /// <param name="operatorName">执行该操作的用户名（审计用）。</param>
    /// <returns>操作结果。</returns>
    public UserOperationResult SetEnabled(int userId, bool enabled, string operatorName)
    {
        try
        {
            var user = _users.GetById(userId);
            if (user is null)
            {
                return UserOperationResult.Failure("用户不存在，可能已被其他窗口删除，请刷新后重试。");
            }

            if (user.IsEnabled == enabled)
            {
                return UserOperationResult.Failure($"用户「{user.UserName}」当前已是{(enabled ? "启用" : "禁用")}状态。");
            }

            if (!enabled)
            {
                if (IsSelf(user, operatorName))
                {
                    return UserOperationResult.Failure("不能禁用当前登录的账户，请用其他工程师账户操作。");
                }

                if (user.Role == UserRole.Engineer && !HasOtherEnabledEngineer(userId))
                {
                    return UserOperationResult.Failure(
                        $"「{user.UserName}」是最后一个启用的工程师账户，禁用后将无人能进入系统设置。");
                }
            }

            user.IsEnabled = enabled;
            _users.Update(user);

            WriteAuditLog(operatorName, enabled ? ActionEnable : ActionDisable, user.UserName,
                          oldValue: enabled ? "禁用" : "启用", newValue: enabled ? "启用" : "禁用");
            _log.LogInformation("用户 {UserName} 已{Action}，操作人 {Operator}",
                                user.UserName, enabled ? "启用" : "禁用", operatorName);
            return UserOperationResult.Success($"已{(enabled ? "启用" : "禁用")}用户「{user.UserName}」。");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "设置用户 {UserId} 启用状态失败", userId);
            return UserOperationResult.Failure("写入用户失败，请检查数据库后重试。");
        }
    }

    /// <summary>删除用户。</summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="operatorName">执行该操作的用户名（审计用）。</param>
    /// <returns>操作结果。</returns>
    public UserOperationResult Delete(int userId, string operatorName)
    {
        try
        {
            var user = _users.GetById(userId);
            if (user is null)
            {
                return UserOperationResult.Failure("用户不存在，可能已被其他窗口删除，请刷新后重试。");
            }

            if (IsSelf(user, operatorName))
            {
                return UserOperationResult.Failure("不能删除当前登录的账户。");
            }

            if (user.IsEnabled && user.Role == UserRole.Engineer && !HasOtherEnabledEngineer(userId))
            {
                return UserOperationResult.Failure(
                    $"「{user.UserName}」是最后一个启用的工程师账户，删除后将无人能进入系统设置。");
            }

            _users.Delete(userId);

            WriteAuditLog(operatorName, ActionDelete, user.UserName, Describe(user.Role), newValue: null);
            _log.LogInformation("删除用户 {UserName}（角色 {Role}），操作人 {Operator}",
                                user.UserName, user.Role, operatorName);
            return UserOperationResult.Success($"已删除用户「{user.UserName}」。");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "删除用户 {UserId} 失败", userId);
            return UserOperationResult.Failure("删除用户失败，请检查数据库后重试。");
        }
    }

    /// <summary>校验登录名格式；返回空字符串表示通过。</summary>
    private static string ValidateUserName(string name)
    {
        if (name.Length == 0)
        {
            return "请输入用户名。";
        }

        if (name.Length < MinUserNameLength || name.Length > MaxUserNameLength)
        {
            return $"用户名长度应在 {MinUserNameLength} ~ {MaxUserNameLength} 个字符之间。";
        }

        foreach (var ch in name)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_')
            {
                return "用户名只能包含字母、数字和下划线（登录时按原样输入）。";
            }
        }

        return string.Empty;
    }

    /// <summary>校验口令格式；返回空字符串表示通过。</summary>
    private static string ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "请输入口令。";
        }

        if (password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
        {
            return $"口令长度应在 {MinPasswordLength} ~ {MaxPasswordLength} 个字符之间。";
        }

        return string.Empty;
    }

    /// <summary>除指定用户外是否还有启用的工程师（防止把系统锁死）。</summary>
    private bool HasOtherEnabledEngineer(int excludedUserId)
        => _users.GetAll().Any(user => user.Id != excludedUserId && user.IsEnabled && user.Role == UserRole.Engineer);

    private static bool IsSelf(User user, string operatorName)
        => string.Equals(user.UserName, operatorName?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>角色显示名（与界面用词一致）。</summary>
    private static string Describe(UserRole role) => role switch
    {
        UserRole.Operator => "操作员",
        UserRole.Technician => "技术员",
        UserRole.Engineer => "工程师",
        _ => "未知角色"
    };

    /// <summary>写审计日志。审计失败只记日志，不让用户管理操作失败（否则工程师无法维护账户）。</summary>
    private void WriteAuditLog(string operatorName, string action, string target, string? oldValue, string? newValue)
    {
        if (string.IsNullOrWhiteSpace(operatorName))
        {
            _log.LogWarning("用户管理操作缺少操作人，审计记录已跳过：{Action} {Target}", action, target);
            return;
        }

        try
        {
            _operationLog.Insert(operatorName, action, target, oldValue, newValue);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "写用户管理审计日志失败：{Action} {Target}", action, target);
        }
    }
}
