// 职责：参数编辑区的一行（中文名 + 输入框 + 单位 + 范围/错误提示），负责即时范围校验
// 层：App/ViewModels
// 依赖：ParameterDescriptor/ParameterCatalog/ParameterValidator（Core）、CommunityToolkit.Mvvm

using System.Globalization;
using AutoTaping.Core.Models;
using AutoTaping.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoTaping.App.ViewModels;

/// <summary>
/// 单个参数的编辑模型。输入即校验（09 文档第五节与第七节约束 4：超范围红框 + 提示范围 + 禁用保存），
/// 校验逻辑全部委托给 Core 的 <see cref="ParameterValidator"/>，界面不重复写范围判断。
/// </summary>
public sealed partial class ParameterItemViewModel : ObservableObject
{
    /// <summary>数字解析失败时的提示。</summary>
    private const string NotANumberText = "请输入数字";

    private readonly ParameterDescriptor _descriptor;

    /// <summary>加载配方参数时抑制「有未保存修改」标记。</summary>
    private bool _suppressChangeEvent;

    /// <summary>构造参数行。</summary>
    /// <param name="descriptor">参数定义。</param>
    /// <param name="value">初始值（来自当前配方）。</param>
    /// <param name="isReadOnly">当前用户是否只能查看（无编辑权限）。</param>
    public ParameterItemViewModel(ParameterDescriptor descriptor, double value, bool isReadOnly)
    {
        _descriptor = descriptor;
        IsReadOnly = isReadOnly;
        _text = ParameterCatalog.Format(descriptor, value);
        Validate();
    }

    /// <summary>用户修改了本参数（加载数据时不触发）。</summary>
    public event EventHandler? ValueChanged;

    /// <summary>参数键名（与 09 文档参数表的键名列一致）。</summary>
    public string Key => _descriptor.Key;

    /// <summary>中文名称。</summary>
    public string DisplayName => _descriptor.DisplayName;

    /// <summary>单位（无单位为空串）。</summary>
    public string Unit => _descriptor.Unit;

    /// <summary>输入框文本。</summary>
    [ObservableProperty]
    private string _text;

    /// <summary>是否超出允许范围（红框 + 禁用保存）。</summary>
    [ObservableProperty]
    private bool _hasError;

    /// <summary>提示文本：合法时显示范围，非法时显示「范围 + 原因」。</summary>
    [ObservableProperty]
    private string _hintText = "";

    /// <summary>是否只读（无参数编辑权限时为 <see langword="true"/>）。</summary>
    [ObservableProperty]
    private bool _isReadOnly;

    /// <summary>
    /// 取当前输入值。文本非法时返回 0 并通过 <paramref name="error"/> 说明原因。
    /// </summary>
    /// <param name="value">解析出的数值。</param>
    /// <param name="error">非法原因；合法时为空串。</param>
    public bool TryGetValue(out double value, out string error)
    {
        if (!double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            error = $"{DisplayName}：{NotANumberText}";
            return false;
        }

        return ParameterValidator.TryValidate(_descriptor, value, out error);
    }

    /// <summary>用配方里的值刷新显示（加载 / 恢复默认 / 保存成功后调用，不标记为「未保存修改」）。</summary>
    /// <param name="value">新值。</param>
    public void LoadValue(double value)
    {
        _suppressChangeEvent = true;
        try
        {
            Text = ParameterCatalog.Format(_descriptor, value);
        }
        finally
        {
            _suppressChangeEvent = false;
        }
    }

    partial void OnTextChanged(string value)
    {
        Validate();

        if (!_suppressChangeEvent)
        {
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>校验当前文本并刷新红框与提示。</summary>
    private void Validate()
    {
        if (!double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            HasError = true;
            HintText = NotANumberText;
            return;
        }

        if (!ParameterValidator.TryValidate(_descriptor, parsed, out var error))
        {
            HasError = true;
            HintText = error;
            return;
        }

        HasError = false;
        HintText = $"范围 {_descriptor.RangeText}";
    }
}
