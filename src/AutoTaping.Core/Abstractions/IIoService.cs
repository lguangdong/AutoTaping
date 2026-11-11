// 职责：IO 服务抽象——带名称的 DI/DO 点位读写与工程师强制输出（签名与 docs/02 第 2 节完全一致，不得私自变更）
// 层：Core/Abstractions
// 依赖：无（纯接口）

namespace AutoTaping.Core.Abstractions;

/// <summary>
/// 带名称的 IO 点位（点位名映射来自 appsettings.json 的 <c>Io:DiMap</c> / <c>Io:DoMap</c>）。
/// 真实实现走运控卡 DI/DO，无硬件时见 <c>AutoTaping.Hardware.Io.VirtualIoService</c>。
/// </summary>
public interface IIoService
{
    /// <summary>读取输入点位，例：<c>"X00"</c>。</summary>
    bool ReadDi(string point);

    /// <summary>回读输出点位。</summary>
    bool ReadDo(string point);

    /// <summary>写输出点位。</summary>
    void WriteDo(string point, bool on);

    /// <summary>批量读取全部输入（供采集线程，减少 P/Invoke 次数）。</summary>
    IReadOnlyDictionary<string, bool> ReadAllDi();

    /// <summary>批量读取全部输出。</summary>
    IReadOnlyDictionary<string, bool> ReadAllDo();

    /// <summary>强制输出（仅工程师权限）。退出手动页必须 <see cref="ClearAllForcedOutput"/>。</summary>
    void ForceOutput(string point, bool on);

    /// <summary>清除全部强制输出。</summary>
    void ClearAllForcedOutput();

    /// <summary>该点位是否处于强制输出状态。</summary>
    bool IsForced(string point);
}
