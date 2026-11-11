// 职责：用户编辑对话框的返回值（登录名、角色、口令），只承载输入，不做任何校验
// 层：App/Infrastructure
// 依赖：UserRole（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// 用户编辑对话框收集到的输入。校验（格式、唯一性、最后一个工程师保护）由 Core 的
/// <c>UserManagementService</c> 负责，界面只负责收集。
/// </summary>
/// <param name="UserName">登录名（已去首尾空白）。</param>
/// <param name="Role">角色。</param>
/// <param name="Password">口令；未修改口令时为空字符串。</param>
public sealed record UserEditInput(string UserName, UserRole Role, string Password);
