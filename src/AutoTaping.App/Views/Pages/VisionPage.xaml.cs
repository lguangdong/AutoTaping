// 职责：视觉标定页的 code-behind——只做鼠标框选坐标捕获并转发给 ViewModel，不含业务逻辑
// 层：App/Views/Pages
// 依赖：VisionViewModel

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AutoTaping.App.Views.Pages;

/// <summary>视觉标定页。预览图 Canvas 上的鼠标拖拽框选由这里捕获坐标后转交 ViewModel（10-编码约定 三、第 7 条）。</summary>
public partial class VisionPage : UserControl
{
    private Point _dragStart;
    private bool _isDragging;

    /// <summary>创建页面。</summary>
    public VisionPage()
    {
        InitializeComponent();
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var canvas = (Canvas)sender;
        _dragStart = e.GetPosition(canvas);
        _isDragging = true;
        canvas.CaptureMouse();
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        var canvas = (Canvas)sender;
        var position = e.GetPosition(canvas);

        var x = Math.Min(_dragStart.X, position.X);
        var y = Math.Min(_dragStart.Y, position.Y);
        var width = Math.Abs(position.X - _dragStart.X);
        var height = Math.Abs(position.Y - _dragStart.Y);

        (DataContext as ViewModels.VisionViewModel)?.SetRoiSelection(x, y, width, height);
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        var canvas = (Canvas)sender;
        canvas.ReleaseMouseCapture();
        _isDragging = false;
    }
}
