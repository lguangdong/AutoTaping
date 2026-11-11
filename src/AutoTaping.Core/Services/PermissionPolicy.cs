// 职责：角色 → 权限点的唯一映射（操作员 / 技术员 / 工程师三级）
// 层：Core/Services
// 依赖：UserRole、Permission（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.Core.Services;

/// <summary>
/// 角色权限策略。权限边界见 <c>docs/00-项目总览.md</c> 第 5 节，页面权限见
/// <c>docs/07-UI设计规范.md</c> 第 5.2 节。所有权限判定必须走本类，不得在界面里各自写 if。
/// </summary>
public static class PermissionPolicy
{
    /// <summary>取某个角色被授予的全部权限点。</summary>
    /// <param name="role">用户角色。</param>
    public static Permission GetGrantedPermissions(UserRole role) => role switch
    {
        // 操作员：只能看主画面、确认报警、切换配方（只读），无受限页面
        UserRole.Operator => Permission.None,

        // 技术员：手动调试、视觉标定、改配方参数（含另存/导入导出）、导出产量明细；
        // IO 强制输出与「全部确认报警」属于工程师（08-页面清单 页面 2 / 页面 5 的权限表）
        UserRole.Technician => Permission.ManualMode | Permission.Calibration
                               | Permission.RecipeEdit | Permission.OutputExport,

        // 工程师：全部权限，含 IO 强制输出、全部确认报警、系统设置
        UserRole.Engineer => Permission.ManualMode | Permission.Calibration | Permission.IoForce
                             | Permission.SystemSettings | Permission.RecipeEdit
                             | Permission.AlarmAckAll | Permission.OutputExport,

        _ => Permission.None
    };

    /// <summary>判定角色是否具备指定权限（支持按位组合，要求全部满足）。</summary>
    /// <param name="role">用户角色。</param>
    /// <param name="permission">权限点；<see cref="Permission.None"/> 对任何角色都成立。</param>
    public static bool HasPermission(UserRole role, Permission permission)
    {
        if (permission == Permission.None)
        {
            return true;
        }

        var granted = GetGrantedPermissions(role);
        return (granted & permission) == permission;
    }

    /// <summary>取某个权限点所需的角色名称，用于「权限不足」提示（例：<c>技术员</c>）。</summary>
    /// <param name="permission">权限点。</param>
    public static string GetRequiredRoleName(Permission permission) => permission switch
    {
        Permission.ManualMode or Permission.Calibration or Permission.RecipeEdit or Permission.OutputExport => "技术员",
        Permission.SystemSettings or Permission.IoForce or Permission.AlarmAckAll => "工程师",
        _ => "操作员"
    };
}
