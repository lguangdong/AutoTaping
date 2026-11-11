// 职责：文件选择对话框的抽象（打开 / 保存），让 ViewModel 不直接依赖 WPF 的文件对话框
// 层：App/Infrastructure
// 依赖：无

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// 文件选择抽象。配方导入/导出、报警与产量 CSV 导出都走本接口：
/// ViewModel 只拿到路径，文件对话框属于表现层细节（10-编码约定 三、第 7 条）。
/// </summary>
public interface IFilePicker
{
    /// <summary>弹出「打开文件」对话框。</summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="filter">文件过滤器，如 <c>配方文件 (*.json)|*.json|所有文件 (*.*)|*.*</c>。</param>
    /// <returns>选中的文件路径；用户取消返回 <see langword="null"/>。</returns>
    string? PickOpenFile(string title, string filter);

    /// <summary>弹出「保存文件」对话框。</summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="filter">文件过滤器。</param>
    /// <param name="defaultFileName">默认文件名（含扩展名）。</param>
    /// <returns>目标文件路径；用户取消返回 <see langword="null"/>。</returns>
    string? PickSaveFile(string title, string filter, string defaultFileName);
}
