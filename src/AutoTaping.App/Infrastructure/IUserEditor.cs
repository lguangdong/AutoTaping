// 职责：用户编辑对话框的抽象（新增用户 / 改角色 / 改密），把弹窗细节留在表现层
// 层：App/Infrastructure
// 依赖：UserEditInput、UserRole（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// 用户编辑抽象。系统设置页需要输入登录名、角色与口令，<see cref="Core.Abstractions.IUserNotifier"/>
/// 只能提示与确认，因此由本接口提供表单式输入，避免 ViewModel 自己开窗口（10-编码约定 三、第 7 条）。
/// </summary>
public interface IUserEditor
{
    /// <summary>弹出用户编辑对话框。</summary>
    /// <param name="title">窗口标题，如「新增用户」。</param>
    /// <param name="userName">登录名初值；改角色 / 改密时传入现有登录名。</param>
    /// <param name="userNameEditable">登录名是否可编辑（仅新增时为 <see langword="true"/>）。</param>
    /// <param name="roleEditable">角色是否可编辑（改密时为 <see langword="false"/>）。</param>
    /// <param name="passwordRequired">口令是否必填（改为可选时，留空表示不改口令）。</param>
    /// <param name="role">角色初值。</param>
    /// <returns>用户输入；用户取消返回 <see langword="null"/>。</returns>
    UserEditInput? Show(
        string title,
        string userName,
        bool userNameEditable,
        bool roleEditable,
        bool passwordRequired,
        UserRole role);
}
