// 职责：PermissionPolicy 单元测试（三级角色的导航权限与所需角色名称），对应 S3 验收标准第 3 条
// 层：Tests
// 依赖：AutoTaping.Core（PermissionPolicy、UserRole、Permission）

using AutoTaping.Core.Models;
using AutoTaping.Core.Services;

namespace AutoTaping.Tests;

/// <summary>
/// <see cref="PermissionPolicy"/> 的测试：操作员不能进手动/标定/系统页，技术员可进手动与标定，
/// 工程师全部可进（权限边界见 00-项目总览 第 5 节）。
/// </summary>
public sealed class PermissionPolicyTests
{
    [Theory]
    [InlineData(UserRole.Operator, Permission.ManualMode, false)]
    [InlineData(UserRole.Operator, Permission.Calibration, false)]
    [InlineData(UserRole.Operator, Permission.SystemSettings, false)]
    [InlineData(UserRole.Operator, Permission.IoForce, false)]
    [InlineData(UserRole.Technician, Permission.ManualMode, true)]
    [InlineData(UserRole.Technician, Permission.Calibration, true)]
    [InlineData(UserRole.Technician, Permission.SystemSettings, false)]
    [InlineData(UserRole.Technician, Permission.IoForce, false)]
    [InlineData(UserRole.Engineer, Permission.ManualMode, true)]
    [InlineData(UserRole.Engineer, Permission.Calibration, true)]
    [InlineData(UserRole.Engineer, Permission.SystemSettings, true)]
    [InlineData(UserRole.Engineer, Permission.IoForce, true)]
    public void HasPermission_MatchesRoleBoundary(UserRole role, Permission permission, bool expected)
        => Assert.Equal(expected, PermissionPolicy.HasPermission(role, permission));

    [Fact]
    public void HasPermission_NoneIsAlwaysAllowed()
    {
        Assert.True(PermissionPolicy.HasPermission(UserRole.Operator, Permission.None));
        Assert.True(PermissionPolicy.HasPermission(UserRole.Technician, Permission.None));
        Assert.True(PermissionPolicy.HasPermission(UserRole.Engineer, Permission.None));
    }

    [Fact]
    public void HasPermission_CombinedPermission_RequiresAllBits()
    {
        var combined = Permission.ManualMode | Permission.SystemSettings;

        Assert.False(PermissionPolicy.HasPermission(UserRole.Technician, combined));
        Assert.True(PermissionPolicy.HasPermission(UserRole.Engineer, combined));
    }

    [Theory]
    [InlineData(Permission.ManualMode, "技术员")]
    [InlineData(Permission.Calibration, "技术员")]
    [InlineData(Permission.SystemSettings, "工程师")]
    [InlineData(Permission.IoForce, "工程师")]
    [InlineData(Permission.None, "操作员")]
    public void GetRequiredRoleName_ReturnsDisplayText(Permission permission, string expected)
        => Assert.Equal(expected, PermissionPolicy.GetRequiredRoleName(permission));
}
