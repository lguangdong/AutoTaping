// 职责：IO 点位名常量（点位电气定义见 00-项目总览 第 1.3 节），业务代码不得散落 "Y00" 之类的字面量
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// IO 点位名常量。取值与 <c>docs/00-项目总览.md</c> 第 1.3 节的电气点表一致，
/// 中文名称由 <c>appsettings.json</c> 的 <c>Io:DiMap</c> / <c>Io:DoMap</c> 提供（08-页面清单 页面 7）。
/// </summary>
public static class IoPointNames
{
    // ---------- DI（输入） ----------

    /// <summary>X00 上料位有料。</summary>
    public const string MaterialAtLoad = "X00";

    /// <summary>X01 贴附位有料。</summary>
    public const string MaterialAtTape = "X01";

    /// <summary>X02 下料位有料。</summary>
    public const string MaterialAtUnload = "X02";

    /// <summary>X03 真空压力开关（吸住确认，报警 AL301 的判据）。</summary>
    public const string VacuumSensor = "X03";

    /// <summary>X04 胶带余量检测（报警 AL303）。</summary>
    public const string TapeRemaining = "X04";

    /// <summary>X05 安全门（闭合 = 1，互锁第 2 条）。</summary>
    public const string SafetyDoorClosed = "X05";

    /// <summary>X06 急停（常闭，正常 = 1；= 0 表示急停被按下，报警 AL100）。</summary>
    public const string EmergencyStopReleased = "X06";

    /// <summary>X07 气压正常（= 1 正常，互锁第 3 条；05-核心状态机 与 00-项目总览的点表用词不同，以 05 的判据为准）。</summary>
    public const string AirPressureOk = "X07";

    // ---------- DO（输出） ----------

    /// <summary>Y00 真空阀。</summary>
    public const string VacuumValve = "Y00";

    /// <summary>Y01 吹气阀（破真空）。</summary>
    public const string BlowValve = "Y01";

    /// <summary>Y02 压合气缸下压。</summary>
    public const string PressCylinder = "Y02";

    /// <summary>Y03 三色灯-绿。</summary>
    public const string TowerLightGreen = "Y03";

    /// <summary>Y04 三色灯-黄。</summary>
    public const string TowerLightYellow = "Y04";

    /// <summary>Y05 三色灯-红。</summary>
    public const string TowerLightRed = "Y05";

    /// <summary>Y06 蜂鸣器。</summary>
    public const string Buzzer = "Y06";
}
