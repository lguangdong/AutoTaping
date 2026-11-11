// 职责：报警级别枚举，取值与 AlarmRecord.Level 列的整数一一对应
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 报警级别（见 <c>docs/09-报警与参数清单.md</c> 第一节）。数值即数据库存储值，不得改动。
/// </summary>
public enum AlarmLevel
{
    /// <summary>提示：仅显示，不停机。</summary>
    Info = 1,

    /// <summary>警告：显示 + 蜂鸣，可继续运行。</summary>
    Warning = 2,

    /// <summary>严重：立即停机 + 蜂鸣 + 禁止启动。</summary>
    Critical = 3
}
