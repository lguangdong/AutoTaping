// 职责：AuthService 单元测试（登录成功/口令错误/禁用账户/登出/权限判定），覆盖 S3 登录与权限控制
// 层：Tests
// 依赖：AutoTaping.Core（AuthService、PasswordHasher、IUserRepository、IOperationLogRepository）

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using AutoTaping.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTaping.Tests;

/// <summary>
/// <see cref="AuthService"/> 的行为测试：口令校验、禁用账户拒绝、当前用户生命周期与权限判定。
/// </summary>
public sealed class AuthServiceTests
{
    /// <summary>正确的口令。</summary>
    private const string ValidPassword = "123456";

    [Fact]
    public void Login_WithCorrectPassword_SetsCurrentUser()
    {
        var user = CreateUser(UserRole.Engineer);
        var service = CreateService(user);

        var result = service.Login(" admin ", ValidPassword);

        Assert.True(result.Succeeded);
        Assert.Equal("", result.Message);
        Assert.True(service.IsAuthenticated);
        Assert.Equal("admin", service.CurrentUser?.UserName);
        Assert.Equal("admin", service.LastUserName);
    }

    [Fact]
    public void Login_WithWrongPassword_IsRejected()
    {
        var service = CreateService(CreateUser(UserRole.Technician));

        var result = service.Login("admin", "wrong");

        Assert.False(result.Succeeded);
        Assert.Equal("用户名或密码错误", result.Message);
        Assert.False(service.IsAuthenticated);
        Assert.Null(service.CurrentUser);
    }

    [Fact]
    public void Login_WhenUserDisabled_IsRejected()
    {
        var user = CreateUser(UserRole.Operator);
        user.IsEnabled = false;
        var service = CreateService(user);

        var result = service.Login("admin", ValidPassword);

        Assert.False(result.Succeeded);
        Assert.Contains("禁用", result.Message, StringComparison.Ordinal);
        Assert.False(service.IsAuthenticated);
    }

    [Fact]
    public void Login_WhenUserMissing_IsRejected()
    {
        var service = CreateService(user: null);

        var result = service.Login("nobody", ValidPassword);

        Assert.False(result.Succeeded);
        Assert.Equal("用户名或密码错误", result.Message);
    }

    [Fact]
    public void Logout_ClearsCurrentUser_AndRaisesEvent()
    {
        var service = CreateService(CreateUser(UserRole.Engineer));
        service.Login("admin", ValidPassword);

        var raised = 0;
        service.CurrentUserChanged += (_, _) => raised++;

        service.Logout();

        Assert.False(service.IsAuthenticated);
        Assert.Null(service.CurrentUser);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void HasPermission_FollowsRolePolicy()
    {
        var user = CreateUser(UserRole.Operator);
        var service = CreateService(user);
        service.Login("admin", ValidPassword);

        Assert.False(service.HasPermission(Permission.ManualMode));
        Assert.True(service.HasPermission(Permission.None));

        service.Logout();
        Assert.False(service.HasPermission(Permission.None));
    }

    private static AuthService CreateService(User? user) => new(
        new FakeUserRepository(user),
        new FakeOperationLogRepository(),
        SystemClock.Instance,
        NullLogger<AuthService>.Instance);

    private static User CreateUser(UserRole role)
    {
        var salt = PasswordHasher.CreateSalt();
        return new User
        {
            Id = 1,
            UserName = "admin",
            Salt = salt,
            PasswordHash = PasswordHasher.Hash(salt, ValidPassword),
            Role = role,
            IsEnabled = true,
            CreatedAt = new DateTime(2026, 11, 7, 8, 0, 0, DateTimeKind.Unspecified)
        };
    }

    /// <summary>只按用户名匹配的内存储用户仓储（忽略大小写，与 SQLite 实现保持一致）。</summary>
    private sealed class FakeUserRepository : IUserRepository
    {
        private readonly User? _user;

        public FakeUserRepository(User? user) => _user = user;

        public int LastLoginUpdateCount { get; private set; }

        public User? GetByUserName(string userName)
            => _user is not null && string.Equals(_user.UserName, userName, StringComparison.OrdinalIgnoreCase)
                ? _user
                : null;

        public void UpdateLastLogin(int userId, DateTime time)
        {
            Assert.Equal(_user?.Id, userId);
            LastLoginUpdateCount++;
        }

        // ---------- S9 用户管理扩展：登录流程不用，测试替身只需保证编译（不支持即抛异常） ----------

        public IReadOnlyList<User> GetAll() => _user is null ? Array.Empty<User>() : new[] { _user };

        public User? GetById(int userId) => _user?.Id == userId ? _user : null;

        public bool ExistsUserName(string userName)
            => _user is not null && string.Equals(_user.UserName, userName, StringComparison.OrdinalIgnoreCase);

        public int Insert(User user) => throw new NotSupportedException("登录测试不涉及新增用户");

        public void Update(User user) => throw new NotSupportedException("登录测试不涉及修改用户");

        public void Delete(int userId) => throw new NotSupportedException("登录测试不涉及删除用户");
    }

    /// <summary>记录插入次数的操作日志仓储。</summary>
    private sealed class FakeOperationLogRepository : IOperationLogRepository
    {
        public List<string> Actions { get; } = new();

        public void Insert(string user, string action, string target, string? oldValue, string? newValue)
            => Actions.Add(action);

        public IReadOnlyList<OperationLog> Query(DateTime from, DateTime to) => Array.Empty<OperationLog>();
    }
}
