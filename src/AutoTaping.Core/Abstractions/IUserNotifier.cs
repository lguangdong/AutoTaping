// 职责：定义"向用户提示 / 警告 / 报错 / 二次确认"的抽象，隔离 Core 与具体 UI 框架
// 层：Core/Abstractions
// 依赖：无（纯接口；Core 不得引用 WPF，见 10-编码约定 三、分层红线 第 4 条）

namespace AutoTaping.Core.Abstractions;

/// <summary>
/// 用户通知抽象。Core 层与 ViewModel 一律通过本接口与用户交互，禁止直接调用 <c>MessageBox</c>。
/// </summary>
public interface IUserNotifier
{
    /// <summary>展示普通提示信息（不需要用户决策）。</summary>
    /// <param name="message">面向操作工的中文提示内容。</param>
    void Info(string message);

    /// <summary>展示警告信息（不阻断当前操作）。</summary>
    /// <param name="message">面向操作工的中文提示内容。</param>
    void Warn(string message);

    /// <summary>展示错误信息（操作被拒绝或执行失败）。</summary>
    /// <param name="message">必须是操作工能看懂的话，不得直接传异常的 <c>ToString()</c>。</param>
    void Error(string message);

    /// <summary>危险操作二次确认，默认按钮为「取消」（见 07-UI设计规范 第 8 节）。</summary>
    /// <param name="title">弹窗标题，说明操作类别。</param>
    /// <param name="message">操作内容 + 影响说明。</param>
    /// <returns>用户确认返回 <see langword="true"/>；取消或关闭返回 <see langword="false"/>。</returns>
    bool Confirm(string title, string message);
}
