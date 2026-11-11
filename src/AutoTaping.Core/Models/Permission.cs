// 职责：权限点枚举——页面与关键操作的访问级别（按位组合，便于按角色批量授予）
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 权限点。角色到权限点的映射见 <see cref="Services.PermissionPolicy"/>。
/// 只收录需要判定的导航级权限与受限操作；后续阶段新增页面或受限操作时在此扩展，
/// 并同步更新 <c>docs/00-项目总览.md</c> 第 5 节的权限边界。
/// </summary>
[Flags]
public enum Permission
{
    /// <summary>无额外权限要求：全部角色可访问。</summary>
    None = 0,

    /// <summary>进入手动调试页（技术员及以上，见 08-页面清单 页面 2）。</summary>
    ManualMode = 1,

    /// <summary>进入视觉标定页（技术员及以上，见 08-页面清单 页面 4）。</summary>
    Calibration = 2,

    /// <summary>进入系统设置页（仅工程师，见 08-页面清单 页面 8）。</summary>
    SystemSettings = 4,

    /// <summary>IO 强制输出（仅工程师，见 08-页面清单 页面 7 与页面 2 的权限表）。</summary>
    IoForce = 8,

    /// <summary>
    /// 修改配方参数 / 另存为 / 导入 / 导出配方（技术员及以上，见 08-页面清单 页面 3：
    /// 查看=操作员，编辑=技术员+）。只读查看与「加载配方」不需要本权限。
    /// </summary>
    RecipeEdit = 16,

    /// <summary>全部确认报警（仅工程师，见 08-页面清单 页面 5 交互规则）。</summary>
    AlarmAckAll = 32,

    /// <summary>导出产量明细 CSV（技术员及以上，见 08-页面清单 页面 6 权限）。</summary>
    OutputExport = 64
}
