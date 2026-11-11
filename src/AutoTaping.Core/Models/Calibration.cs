// 职责：标定记录实体（对应 SQLite Calibration 表），保存九点标定得到的仿射矩阵与残差
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 标定记录。矩阵含义见 <c>docs/04-视觉与标定.md</c> 第 2.1 节：
/// <c>[X;Y] = [a b c; d e f] · [u;v;1]</c>。
/// </summary>
public sealed class Calibration
{
    /// <summary>仿射矩阵元素个数（a b c d e f）。</summary>
    public const int AffineElementCount = 6;

    /// <summary>主键，新增时由数据库生成。</summary>
    public int Id { get; set; }

    /// <summary>仿射矩阵元素，顺序固定为 a b c d e f（对应表列 MatrixA ~ MatrixF）。</summary>
    public double[] AffineMatrix { get; set; } = new double[AffineElementCount];

    /// <summary>标定残差 RMS（mm），判据见 04 文档 2.4 节。</summary>
    public double RmsError { get; set; }

    /// <summary>参与拟合的点数，至少 3。</summary>
    public int PointCount { get; set; }

    /// <summary>标定原图目录（相对程序根目录）。</summary>
    public string ImageFolder { get; set; } = "";

    /// <summary>是否为当前生效的标定结果。</summary>
    public bool IsActive { get; set; }

    /// <summary>标定时间。</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>标定人用户名。</summary>
    public string CreatedBy { get; set; } = "";
}
