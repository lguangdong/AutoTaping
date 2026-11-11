// 职责：配方参数定义表与读写访问器——键名/中文名/单位/范围/默认值集中一处，参数页与校验器共用
// 层：Core/Services
// 依赖：ParameterDescriptor/ParameterGroup/Permission/RecipeParameters（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.Core.Services;

/// <summary>
/// 配方参数目录。范围与默认值取自 <c>docs/09-报警与参数清单.md</c> 第四节参数表：
/// <list type="bullet">
/// <item>工艺参数范围与默认值见 4.1 节（全部为热生效参数）。</item>
/// <item>位置参数范围取 <c>docs/00-项目总览.md</c> 1.2 节的示例行程（实际行程以 appsettings 为准）。</item>
/// <item>视觉参数的范围见 4.3 节；ROI 与模板路径由视觉标定页维护，不在此重复编辑。</item>
/// </list>
/// 本类只做「定义 + 读写」，不做校验（校验见 <see cref="ParameterValidator"/>）。
/// </summary>
public static class ParameterCatalog
{
    /// <summary>相机默认图像宽度（像素），用于视觉参数里的像素坐标范围。</summary>
    private const double ImageWidthPx = 1280;

    /// <summary>相机默认图像高度（像素）。</summary>
    private const double ImageHeightPx = 1024;

    /// <summary>X / Y 轴示例行程上限（mm，00 文档 1.2 节）。</summary>
    private const double AxisTravelMm = 400;

    private static readonly IReadOnlyList<ParameterDescriptor> Descriptors =
    [
        // ---------- 工艺参数（09 文档 4.1） ----------
        new("safeHeightMm", "安全高度", "mm", ParameterGroup.Process, 10, 80, 50.0, 1),
        new("tapeHeightMm", "贴附高度", "mm", ParameterGroup.Process, 0, 10, 2.0, 1),
        new("moveSpeedMmPerSec", "移动速度", "mm/s", ParameterGroup.Process, 10, 300, 100.0, 1),
        new("rotateSpeedDegPerSec", "旋转速度", "°/s", ParameterGroup.Process, 10, 180, 90.0, 1),
        new("holdPressureMs", "保压时间", "ms", ParameterGroup.Process, 100, 3000, 500, 0),
        new("holdPressureMpa", "贴附压力", "MPa", ParameterGroup.Process, 0.10, 0.40, 0.25, 2),
        new("vacuumWaitMs", "真空等待", "ms", ParameterGroup.Process, 500, 5000, 1500, 0),
        new("blowOffMs", "吹气时间", "ms", ParameterGroup.Process, 100, 1000, 300, 0),

        // ---------- 位置参数（09 文档 4.2，范围为轴软限位内） ----------
        new("photoPosX", "拍照位 X", "mm", ParameterGroup.Position, 0, AxisTravelMm, 156.0, 2),
        new("photoPosY", "拍照位 Y", "mm", ParameterGroup.Position, 0, AxisTravelMm, 200.0, 2),
        new("tapeBaseX", "贴附基准 X", "mm", ParameterGroup.Position, 0, AxisTravelMm, 200.0, 2),
        new("tapeBaseY", "贴附基准 Y", "mm", ParameterGroup.Position, 0, AxisTravelMm, 200.0, 2),
        new("tapeBaseR", "贴附基准 R", "°", ParameterGroup.Position, -180, 180, 0.0, 2),
        new("pickPosX", "取料位 X", "mm", ParameterGroup.Position, 0, AxisTravelMm, 200.0, 2),
        new("pickPosY", "取料位 Y", "mm", ParameterGroup.Position, 0, AxisTravelMm, 80.0, 2),
        new("cameraOffsetX", "相机偏置 X", "mm", ParameterGroup.Position, -200, 200, 0.0, 2,
            Permission.RecipeEdit, "手眼标定值：吸嘴中心 − 相机光轴，改动后必须重新验证拍照位"),
        new("cameraOffsetY", "相机偏置 Y", "mm", ParameterGroup.Position, -200, 200, 44.0, 2,
            Permission.RecipeEdit, "手眼标定值：吸嘴中心 − 相机光轴，改动后必须重新验证拍照位"),
        new("rotationCenterX", "旋转中心 X", "mm", ParameterGroup.Position, 0, AxisTravelMm, 200.0, 2),
        new("rotationCenterY", "旋转中心 Y", "mm", ParameterGroup.Position, 0, AxisTravelMm, 200.0, 2),

        // ---------- 视觉参数（09 文档 4.3） ----------
        new("templateMark1U", "MARK1 基准 U", "px", ParameterGroup.Vision, 0, ImageWidthPx, 400.0, 1),
        new("templateMark1V", "MARK1 基准 V", "px", ParameterGroup.Vision, 0, ImageHeightPx, 500.0, 1),
        new("templateMark2U", "MARK2 基准 U", "px", ParameterGroup.Vision, 0, ImageWidthPx, 880.0, 1),
        new("templateMark2V", "MARK2 基准 V", "px", ParameterGroup.Vision, 0, ImageHeightPx, 500.0, 1),
        new("matchThreshold", "匹配阈值", "", ParameterGroup.Vision, 0.5, 0.95, 0.7, 2,
            Permission.RecipeEdit, "过低会误识别，过高会频繁失败"),
        new("maxDeltaXMm", "ΔX 上限", "mm", ParameterGroup.Vision, 1, 20, 5.0, 1,
            Permission.RecipeEdit, "过大会失去防撞保护"),
        new("maxDeltaYMm", "ΔY 上限", "mm", ParameterGroup.Vision, 1, 20, 5.0, 1,
            Permission.RecipeEdit, "过大会失去防撞保护"),
        new("maxDeltaThetaDeg", "Δθ 上限", "°", ParameterGroup.Vision, 1, 30, 10.0, 1,
            Permission.RecipeEdit, "过大会失去防撞保护"),

        // ---------- 报警阈值（06 文档 4 节 alarm 节点；范围按工艺常识收紧，待现场确认） ----------
        new("vacuumTimeoutMs", "真空吸住超时", "ms", ParameterGroup.Alarm, 500, 5000, 1500, 0),
        new("positionToleranceMm", "位置容差", "mm", ParameterGroup.Alarm, 0.01, 0.5, 0.05, 2)
    ];

    /// <summary>全部参数定义（按分组顺序排列）。</summary>
    public static IReadOnlyList<ParameterDescriptor> All => Descriptors;

    /// <summary>取某个分组的参数定义。</summary>
    /// <param name="group">分组。</param>
    public static IReadOnlyList<ParameterDescriptor> GetByGroup(ParameterGroup group)
        => Descriptors.Where(descriptor => descriptor.Group == group).ToList();

    /// <summary>按键名查找参数定义；键名不存在返回 <see langword="null"/>。</summary>
    /// <param name="key">参数键名。</param>
    public static ParameterDescriptor? Find(string key)
        => Descriptors.FirstOrDefault(descriptor => string.Equals(descriptor.Key, key, StringComparison.Ordinal));

    /// <summary>读取配方中某个参数的当前值。</summary>
    /// <param name="parameters">配方参数。</param>
    /// <param name="key">参数键名。</param>
    /// <param name="value">参数值。</param>
    /// <returns>键名存在返回 <see langword="true"/>。</returns>
    public static bool TryGetValue(RecipeParameters parameters, string key, out double value)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        switch (key)
        {
            case "safeHeightMm": value = parameters.Process.SafeHeightMm; return true;
            case "tapeHeightMm": value = parameters.Process.TapeHeightMm; return true;
            case "moveSpeedMmPerSec": value = parameters.Process.MoveSpeedMmPerSec; return true;
            case "rotateSpeedDegPerSec": value = parameters.Process.RotateSpeedDegPerSec; return true;
            case "holdPressureMs": value = parameters.Process.HoldPressureMs; return true;
            case "holdPressureMpa": value = parameters.Process.HoldPressureMpa; return true;
            case "vacuumWaitMs": value = parameters.Process.VacuumWaitMs; return true;
            case "blowOffMs": value = parameters.Process.BlowOffMs; return true;

            case "photoPosX": value = parameters.Position.PhotoPosX; return true;
            case "photoPosY": value = parameters.Position.PhotoPosY; return true;
            case "tapeBaseX": value = parameters.Position.TapeBaseX; return true;
            case "tapeBaseY": value = parameters.Position.TapeBaseY; return true;
            case "tapeBaseR": value = parameters.Position.TapeBaseR; return true;
            case "pickPosX": value = parameters.Position.PickPosX; return true;
            case "pickPosY": value = parameters.Position.PickPosY; return true;
            case "cameraOffsetX": value = parameters.Position.CameraOffsetX; return true;
            case "cameraOffsetY": value = parameters.Position.CameraOffsetY; return true;
            case "rotationCenterX": value = parameters.Position.RotationCenterX; return true;
            case "rotationCenterY": value = parameters.Position.RotationCenterY; return true;

            case "templateMark1U": value = parameters.Vision.TemplateMark1U; return true;
            case "templateMark1V": value = parameters.Vision.TemplateMark1V; return true;
            case "templateMark2U": value = parameters.Vision.TemplateMark2U; return true;
            case "templateMark2V": value = parameters.Vision.TemplateMark2V; return true;
            case "matchThreshold": value = parameters.Vision.MatchThreshold; return true;
            case "maxDeltaXMm": value = parameters.Vision.MaxDeltaXMm; return true;
            case "maxDeltaYMm": value = parameters.Vision.MaxDeltaYMm; return true;
            case "maxDeltaThetaDeg": value = parameters.Vision.MaxDeltaThetaDeg; return true;

            case "vacuumTimeoutMs": value = parameters.Alarm.VacuumTimeoutMs; return true;
            case "positionToleranceMm": value = parameters.Alarm.PositionToleranceMm; return true;

            default:
                value = 0;
                return false;
        }
    }

    /// <summary>写入配方中某个参数的值（整数参数自动四舍五入取整）。</summary>
    /// <param name="parameters">配方参数。</param>
    /// <param name="key">参数键名。</param>
    /// <param name="value">新值。</param>
    /// <returns>键名存在返回 <see langword="true"/>。</returns>
    public static bool SetValue(RecipeParameters parameters, string key, double value)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        switch (key)
        {
            case "safeHeightMm": parameters.Process.SafeHeightMm = value; return true;
            case "tapeHeightMm": parameters.Process.TapeHeightMm = value; return true;
            case "moveSpeedMmPerSec": parameters.Process.MoveSpeedMmPerSec = value; return true;
            case "rotateSpeedDegPerSec": parameters.Process.RotateSpeedDegPerSec = value; return true;
            case "holdPressureMs": parameters.Process.HoldPressureMs = ToInt(value); return true;
            case "holdPressureMpa": parameters.Process.HoldPressureMpa = value; return true;
            case "vacuumWaitMs": parameters.Process.VacuumWaitMs = ToInt(value); return true;
            case "blowOffMs": parameters.Process.BlowOffMs = ToInt(value); return true;

            case "photoPosX": parameters.Position.PhotoPosX = value; return true;
            case "photoPosY": parameters.Position.PhotoPosY = value; return true;
            case "tapeBaseX": parameters.Position.TapeBaseX = value; return true;
            case "tapeBaseY": parameters.Position.TapeBaseY = value; return true;
            case "tapeBaseR": parameters.Position.TapeBaseR = value; return true;
            case "pickPosX": parameters.Position.PickPosX = value; return true;
            case "pickPosY": parameters.Position.PickPosY = value; return true;
            case "cameraOffsetX": parameters.Position.CameraOffsetX = value; return true;
            case "cameraOffsetY": parameters.Position.CameraOffsetY = value; return true;
            case "rotationCenterX": parameters.Position.RotationCenterX = value; return true;
            case "rotationCenterY": parameters.Position.RotationCenterY = value; return true;

            case "templateMark1U": parameters.Vision.TemplateMark1U = value; return true;
            case "templateMark1V": parameters.Vision.TemplateMark1V = value; return true;
            case "templateMark2U": parameters.Vision.TemplateMark2U = value; return true;
            case "templateMark2V": parameters.Vision.TemplateMark2V = value; return true;
            case "matchThreshold": parameters.Vision.MatchThreshold = value; return true;
            case "maxDeltaXMm": parameters.Vision.MaxDeltaXMm = value; return true;
            case "maxDeltaYMm": parameters.Vision.MaxDeltaYMm = value; return true;
            case "maxDeltaThetaDeg": parameters.Vision.MaxDeltaThetaDeg = value; return true;

            case "vacuumTimeoutMs": parameters.Alarm.VacuumTimeoutMs = ToInt(value); return true;
            case "positionToleranceMm": parameters.Alarm.PositionToleranceMm = value; return true;

            default:
                return false;
        }
    }

    /// <summary>按参数定义的小数位格式化数值（界面输入框的显示文本）。</summary>
    /// <param name="descriptor">参数定义。</param>
    /// <param name="value">参数值。</param>
    public static string Format(ParameterDescriptor descriptor, double value)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return value.ToString($"F{descriptor.Decimals}", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int ToInt(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
