// 职责：运控卡停止原因的错误码常量与报警码映射（对应 docs/02 第 4.2 节停止原因映射表）
// 层：Hardware/Motion
// 依赖：无

namespace AutoTaping.Hardware.Motion;

/// <summary>
/// 运控卡停止原因的错误码常量与报警码映射。
/// 停止原因取值与 <see cref="AutoTaping.Core.Abstractions.IMotionCard.GetStopReason"/> 一致，
/// 报警码见 <c>docs/09-报警与参数清单.md</c> 第二节 AL1xx 运动控制。
/// </summary>
public static class MotionErrorCodes
{
    /// <summary>正常停止，无报警。</summary>
    public const int Normal = 0;

    /// <summary>正/负限位触发，报警 AL101。</summary>
    public const int Limit = 1;

    /// <summary>急停，报警 AL100。</summary>
    public const int EmergencyStop = 2;

    /// <summary>软限位，报警 AL102。</summary>
    public const int SoftLimit = 3;

    /// <summary>驱动报警，报警 AL103。</summary>
    public const int DriveFault = 4;

    /// <summary>把停止原因映射为报警码；未知原因返回空串。</summary>
    /// <param name="stopReason">停止原因（取值同本类常量）。</param>
    public static string GetAlarmCode(int stopReason) => stopReason switch
    {
        Limit => "AL101",
        EmergencyStop => "AL100",
        SoftLimit => "AL102",
        DriveFault => "AL103",
        _ => "",
    };

    /// <summary>把停止原因映射为面向操作工的中文描述。</summary>
    /// <param name="stopReason">停止原因（取值同本类常量）。</param>
    public static string GetDescription(int stopReason) => stopReason switch
    {
        Normal => "正常停止",
        Limit => "限位触发",
        EmergencyStop => "急停",
        SoftLimit => "软限位",
        DriveFault => "驱动报警",
        _ => $"未知停止原因({stopReason})",
    };
}
