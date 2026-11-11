// 职责：文本输入对话框的 code-behind（设置标题/标签/默认文本，回车或确定返回输入内容）
// 层：App/Views/Dialogs
// 依赖：无（业务校验由调用方 ViewModel 负责）

using System.Windows;

namespace AutoTaping.App.Views.Dialogs;

/// <summary>
/// 单行文本输入对话框。只收集文本，名称是否重复等业务规则由调用方判断
/// （10-编码约定 三、第 7 条：View 的 code-behind 不写业务）。
/// </summary>
public partial class TextInputDialog : Window
{
    /// <summary>创建对话框。</summary>
    public TextInputDialog()
    {
        InitializeComponent();
    }

    /// <summary>用户输入的文本（已去除首尾空白）；取消时为 <see langword="null"/>。</summary>
    public string? InputText { get; private set; }

    /// <summary>用标题、标签与默认文本初始化对话框内容。</summary>
    /// <param name="title">窗口标题。</param>
    /// <param name="label">输入项说明。</param>
    /// <param name="defaultValue">默认文本。</param>
    public void Configure(string title, string label, string defaultValue)
    {
        Title = title;
        LabelText.Text = label;
        TextInputBox.Text = defaultValue;
        TextInputBox.SelectAll();
    }

    /// <summary>确定：把输入内容写入 <see cref="InputText"/> 并关闭对话框。</summary>
    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        var text = TextInputBox.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            // 空输入不关闭：让用户补全或按取消
            TextInputBox.Focus();
            return;
        }

        InputText = text;
        DialogResult = true;
    }
}
