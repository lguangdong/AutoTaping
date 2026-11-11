// 职责：参数页左侧分组树的一项（分组名 + 参数个数）
// 层：App/ViewModels
// 依赖：ParameterGroup（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.App.ViewModels;

/// <summary>
/// 参数分组树节点。<c>docs/08-页面清单与交互.md</c> 页面 3 左侧「参数分组树」的显示模型，
/// 分组名用 08 文档的用词。
/// </summary>
public sealed class ParameterGroupItemViewModel
{
    /// <summary>构造分组节点。</summary>
    /// <param name="group">分组枚举。</param>
    /// <param name="parameterCount">该组可编辑的标量参数个数。</param>
    public ParameterGroupItemViewModel(ParameterGroup group, int parameterCount)
    {
        Group = group;
        ParameterCount = parameterCount;
    }

    /// <summary>分组枚举。</summary>
    public ParameterGroup Group { get; }

    /// <summary>该组可编辑参数个数。</summary>
    public int ParameterCount { get; }

    /// <summary>分组中文名（08-页面清单 页面 3）。</summary>
    public string DisplayName => Group switch
    {
        ParameterGroup.Process => "工艺参数",
        ParameterGroup.Position => "位置参数",
        ParameterGroup.Vision => "视觉参数",
        ParameterGroup.Alarm => "报警阈值",
        _ => Group.ToString()
    };

    /// <summary>树节点显示文本，形如 <c>工艺参数（9）</c>。</summary>
    public string DisplayText => $"{DisplayName}（{ParameterCount}）";
}
