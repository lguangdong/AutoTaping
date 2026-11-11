// 职责：停机原因分类代码与中文名（05-核心状态机 第 7.2 节），停机记录与产量页停机分析共用
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 停机原因分类。代码与 <c>docs/05-核心状态机与流程.md</c> 第 7.2 节一一对应，一旦发布不得复用。
/// </summary>
public static class DowntimeReasons
{
    /// <summary>待料：<c>WaitMaterial</c> 超时。</summary>
    public const string WaitMaterial = "WAIT_MATERIAL";

    /// <summary>故障停机：任何报警导致的停止。</summary>
    public const string Fault = "FAULT";

    /// <summary>人工暂停：用户点暂停。</summary>
    public const string ManualPause = "MANUAL_PAUSE";

    /// <summary>换型：切换配方。</summary>
    public const string ChangeRecipe = "CHANGE_RECIPE";

    /// <summary>断电：启动时检测到上次非正常关闭（06 文档第 8 节）。</summary>
    public const string PowerOff = "POWER_OFF";

    /// <summary>全部停机原因（产量页停机分析按此顺序显示）。</summary>
    public static IReadOnlyList<string> All { get; } = [WaitMaterial, Fault, ManualPause, ChangeRecipe, PowerOff];

    /// <summary>停机原因代码转中文（界面显示用）。</summary>
    /// <param name="reason">停机原因代码。</param>
    public static string GetDisplayName(string reason) => reason switch
    {
        WaitMaterial => "待料",
        Fault => "故障停机",
        ManualPause => "人工暂停",
        ChangeRecipe => "换型",
        PowerOff => "断电",
        _ => reason
    };
}
