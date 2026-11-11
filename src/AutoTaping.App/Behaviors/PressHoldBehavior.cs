// 职责：按钮「按住执行 / 松开停止」附加行为（手动页点动按钮用），只转发命令，不含业务判断
// 层：App/Behaviors
// 依赖：System.Windows（DependencyProperty / UIElement）

using System.Windows;
using System.Windows.Input;

namespace AutoTaping.App.Behaviors;

/// <summary>
/// 按下执行 <see cref="PressCommandProperty"/>、松开执行 <see cref="ReleaseCommandProperty"/> 的附加行为。
/// 08-页面清单 页面 2 要求「按住才动，松开立即停」，用普通 Button 的 Click 做不到，故用附加属性挂鼠标/触摸事件。
/// 鼠标捕获丢失（拖出按钮、窗口失去焦点）同样视为松开，保证不会出现"手松了轴还在动"。
/// </summary>
public static class PressHoldBehavior
{
    /// <summary>按下时执行的命令。</summary>
    public static readonly DependencyProperty PressCommandProperty =
        DependencyProperty.RegisterAttached(
            "PressCommand", typeof(ICommand), typeof(PressHoldBehavior),
            new PropertyMetadata(null, OnCommandChanged));

    /// <summary>松开（含捕获丢失）时执行的命令。</summary>
    public static readonly DependencyProperty ReleaseCommandProperty =
        DependencyProperty.RegisterAttached(
            "ReleaseCommand", typeof(ICommand), typeof(PressHoldBehavior),
            new PropertyMetadata(null, OnCommandChanged));

    /// <summary>内部状态：当前是否处于「按下未松开」（防止按下/松开重复触发）。</summary>
    private static readonly DependencyProperty IsPressingProperty =
        DependencyProperty.RegisterAttached(
            "IsPressing", typeof(bool), typeof(PressHoldBehavior), new PropertyMetadata(false));

    /// <summary>读取按下命令。</summary>
    /// <param name="element">目标元素。</param>
    public static ICommand? GetPressCommand(DependencyObject element)
        => (ICommand?)element.GetValue(PressCommandProperty);

    /// <summary>设置按下命令。</summary>
    /// <param name="element">目标元素。</param>
    /// <param name="value">命令。</param>
    public static void SetPressCommand(DependencyObject element, ICommand? value)
        => element.SetValue(PressCommandProperty, value);

    /// <summary>读取松开命令。</summary>
    /// <param name="element">目标元素。</param>
    public static ICommand? GetReleaseCommand(DependencyObject element)
        => (ICommand?)element.GetValue(ReleaseCommandProperty);

    /// <summary>设置松开命令。</summary>
    /// <param name="element">目标元素。</param>
    /// <param name="value">命令。</param>
    public static void SetReleaseCommand(DependencyObject element, ICommand? value)
        => element.SetValue(ReleaseCommandProperty, value);

    /// <summary>两个附加属性任一变化时（重新）挂接事件；先摘再挂保证不会重复订阅。</summary>
    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        element.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
        element.LostMouseCapture -= OnLostMouseCapture;
        element.PreviewTouchDown -= OnPreviewTouchDown;
        element.PreviewTouchUp -= OnPreviewTouchUp;
        element.TouchLeave -= OnPreviewTouchUp;

        element.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        element.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        element.LostMouseCapture += OnLostMouseCapture;
        element.PreviewTouchDown += OnPreviewTouchDown;
        element.PreviewTouchUp += OnPreviewTouchUp;
        element.TouchLeave += OnPreviewTouchUp;
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Press(sender as UIElement);

    private static void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Release(sender as UIElement);

    private static void OnLostMouseCapture(object sender, MouseEventArgs e) => Release(sender as UIElement);

    private static void OnPreviewTouchDown(object? sender, TouchEventArgs e) => Press(sender as UIElement);

    private static void OnPreviewTouchUp(object? sender, TouchEventArgs e) => Release(sender as UIElement);

    private static void Press(UIElement? element)
    {
        if (element is null || (bool)element.GetValue(IsPressingProperty))
        {
            return;
        }

        var command = GetPressCommand(element);
        if (command is null || !command.CanExecute(null))
        {
            return;
        }

        element.SetValue(IsPressingProperty, true);
        command.Execute(null);
    }

    private static void Release(UIElement? element)
    {
        if (element is null || !(bool)element.GetValue(IsPressingProperty))
        {
            return;
        }

        element.SetValue(IsPressingProperty, false);

        var command = GetReleaseCommand(element);

        // 松开命令（停止运动）即使按钮已变灰也必须执行，否则会出现"轴停不下来"
        if (command is not null && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
