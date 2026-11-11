// 职责：页面标识枚举——既是导航顺序，也是内容区 DataTemplate 的切换依据
// 层：App/Navigation
// 依赖：无

namespace AutoTaping.App.Navigation;

/// <summary>
/// 页面标识。数值即导航栏从上到下的顺序，必须与
/// <c>docs/07-UI设计规范.md</c> 第 5.2 节的导航表顺序一致。
/// </summary>
public enum PageKey
{
    /// <summary>自动运行（主画面），对应 AutoPage。</summary>
    Auto = 0,

    /// <summary>手动调试，对应 ManualPage。</summary>
    Manual = 1,

    /// <summary>参数配方，对应 RecipePage。</summary>
    Recipe = 2,

    /// <summary>视觉标定，对应 VisionPage。</summary>
    Vision = 3,

    /// <summary>报警，对应 AlarmPage。</summary>
    Alarm = 4,

    /// <summary>产量统计，对应 OutputPage。</summary>
    Output = 5,

    /// <summary>IO 监控，对应 IoPage。</summary>
    Io = 6,

    /// <summary>系统设置，对应 SystemPage。</summary>
    System = 7
}
