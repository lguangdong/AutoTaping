// 职责：用户角色枚举，取值与 User.Role 列的整数一一对应
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 用户角色（见 <c>docs/00-项目总览.md</c> 第 5 节）。数值即数据库存储值，不得改动。
/// </summary>
public enum UserRole
{
    /// <summary>操作员：只能查看主画面、启动/暂停/停止、确认报警、切换配方。</summary>
    Operator = 1,

    /// <summary>技术员：额外可进手动模式、改工艺参数、标定、查看历史报警。</summary>
    Technician = 2,

    /// <summary>工程师：全部权限，含 IO 强制输出、系统设置、删除配方、退出程序。</summary>
    Engineer = 3
}
