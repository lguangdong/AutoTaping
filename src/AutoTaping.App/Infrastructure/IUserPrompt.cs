// 职责：需要用户输入文本时的抽象（如配方「另存为」的名称输入），把弹窗细节留在表现层
// 层：App/Infrastructure
// 依赖：无

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// 文本输入提示抽象。<see cref="Core.Abstractions.IUserNotifier"/> 只覆盖提示/警告/报错/确认，
/// 需要输入内容的场景（配方另存为的名称）由本接口提供，避免 ViewModel 自己开窗口。
/// </summary>
public interface IUserPrompt
{
    /// <summary>弹出单行文本输入框。</summary>
    /// <param name="title">标题。</param>
    /// <param name="label">输入项说明，如 <c>配方名称</c>。</param>
    /// <param name="defaultValue">默认文本。</param>
    /// <returns>用户输入的文本（已 Trim）；取消返回 <see langword="null"/>。</returns>
    string? PromptText(string title, string label, string defaultValue);
}
