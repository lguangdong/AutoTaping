// 职责：UserManagementService 单元测试——新增 / 唯一性 / 口令 / 最后一个工程师保护 / 审计日志（S9 用户管理）
// 层：Tests
// 依赖：AutoTaping.Core（UserManagementService、PasswordHasher）、AutoTaping.Data、Tests/Infrastructure/TestDatabase

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using AutoTaping.Core.Services;
using AutoTaping.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTaping.Tests;

/// <summary>
/// <see cref="UserManagementService"/> 的测试。种子库里已有 admin（工程师、口令 123456），
/// 因此「最后一个启用的工程师」保护、禁用自己等规则都能直接验证。
/// </summary>
public sealed class UserManagementServiceTests
{
    /// <summary>审计用的操作人（不能与 admin 同名，否则会命中「不能禁用/删除自己」的分支）。</summary>
    private const string OtherEngineer = "eng02";

    [Fact]
    public void Create_WritesUserWithSaltedHash_AndAuditLog()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var users = database.CreateUserRepository();
        var service = CreateService(database, users);

        var result = service.Create("tech01", "abc123", UserRole.Technician, "admin");

        Assert.True(result.Succeeded);

        var created = users.GetByUserName("tech01");
        Assert.NotNull(created);
        Assert.Equal(UserRole.Technician, created!.Role);
        Assert.True(created.IsEnabled);
        Assert.NotEqual("abc123", created.PasswordHash);
        Assert.True(PasswordHasher.Verify(created.Salt, "abc123", created.PasswordHash));

        var logs = database.CreateOperationLogRepository().Query(DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1));
        Assert.Contains(logs, log => log.Action == "新增用户" && log.Target == "tech01");
    }

    [Fact]
    public void Create_DuplicateUserName_IsRejectedCaseInsensitively()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var service = CreateService(database, database.CreateUserRepository());

        Assert.True(service.Create("tech01", "abc123", UserRole.Technician, "admin").Succeeded);

        var duplicate = service.Create("TECH01", "abc123", UserRole.Technician, "admin");

        Assert.False(duplicate.Succeeded);
        Assert.Contains("已存在", duplicate.Message);
    }

    [Fact]
    public void Create_ShortPassword_IsRejected()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var service = CreateService(database, database.CreateUserRepository());

        var result = service.Create("tech01", "123", UserRole.Technician, "admin");

        Assert.False(result.Succeeded);
        Assert.Contains("口令长度", result.Message);
    }

    [Fact]
    public void ResetPassword_MakesOldPasswordInvalid()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var users = database.CreateUserRepository();
        var service = CreateService(database, users);
        service.Create("tech01", "abc123", UserRole.Technician, "admin");
        var created = users.GetByUserName("tech01")!;

        var result = service.ResetPassword(created.Id, "new12345", "admin");

        Assert.True(result.Succeeded);
        var updated = users.GetById(created.Id)!;
        Assert.False(PasswordHasher.Verify(updated.Salt, "abc123", updated.PasswordHash));
        Assert.True(PasswordHasher.Verify(updated.Salt, "new12345", updated.PasswordHash));
    }

    [Fact]
    public void SetEnabled_LastEnabledEngineer_IsRejected()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var users = database.CreateUserRepository();
        var service = CreateService(database, users);
        var admin = users.GetByUserName("admin")!;

        var result = service.SetEnabled(admin.Id, enabled: false, OtherEngineer);

        Assert.False(result.Succeeded);
        Assert.Contains("最后一个启用的工程师", result.Message);
    }

    [Fact]
    public void SetEnabled_Self_IsRejected()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var users = database.CreateUserRepository();
        var service = CreateService(database, users);
        service.Create(OtherEngineer, "abc123", UserRole.Engineer, "admin");
        var admin = users.GetByUserName("admin")!;

        var result = service.SetEnabled(admin.Id, enabled: false, "admin");

        Assert.False(result.Succeeded);
        Assert.Contains("当前登录的账户", result.Message);
    }

    [Fact]
    public void SetEnabled_WithAnotherEngineer_Succeeds()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var users = database.CreateUserRepository();
        var service = CreateService(database, users);
        service.Create(OtherEngineer, "abc123", UserRole.Engineer, "admin");
        var admin = users.GetByUserName("admin")!;

        var result = service.SetEnabled(admin.Id, enabled: false, OtherEngineer);

        Assert.True(result.Succeeded);
        Assert.False(users.GetById(admin.Id)!.IsEnabled);
    }

    [Fact]
    public void ChangeRole_LastEngineer_IsRejected()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var users = database.CreateUserRepository();
        var service = CreateService(database, users);
        var admin = users.GetByUserName("admin")!;

        var result = service.ChangeRole(admin.Id, UserRole.Operator, OtherEngineer);

        Assert.False(result.Succeeded);
        Assert.Contains("最后一个启用的工程师", result.Message);
        Assert.Equal(UserRole.Engineer, users.GetById(admin.Id)!.Role);
    }

    [Fact]
    public void Delete_LastEngineer_IsRejected_ButOperatorCanBeDeleted()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var users = database.CreateUserRepository();
        var service = CreateService(database, users);
        var admin = users.GetByUserName("admin")!;

        Assert.False(service.Delete(admin.Id, OtherEngineer).Succeeded);

        service.Create("op01", "abc123", UserRole.Operator, "admin");
        var operatorUser = users.GetByUserName("op01")!;

        Assert.True(service.Delete(operatorUser.Id, OtherEngineer).Succeeded);
        Assert.Null(users.GetByUserName("op01"));
    }

    private static UserManagementService CreateService(TestDatabase database, IUserRepository users)
        => new(users,
               database.CreateOperationLogRepository(),
               database.Clock,
               NullLogger<UserManagementService>.Instance);
}
