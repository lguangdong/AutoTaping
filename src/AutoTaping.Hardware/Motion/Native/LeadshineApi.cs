// 职责：雷赛 LTDMC.dll 的 P/Invoke 声明集中处（02 文档第 7 节：所有 DllImport 只能出现在本文件）
// 层：Hardware/Motion/Native
// 依赖：LTDMC.dll（原生）

using System.Runtime.InteropServices;

namespace AutoTaping.Hardware.Motion.Native;

/// <summary>
/// 雷赛运动控制卡原生 API 声明（<c>LTDMC.dll</c>），以 DMC2410 系列官方手册为参考（02 文档第 3 节）。
/// 型号差异（DMC5800 / SMC6490 / DMC-E）只允许在本文件内调整，不得动 <see cref="IMotionCard"/> 接口。
/// 返回值约定：
/// - 板卡/轴参数/运动/回零/软限位/写 IO 类函数返回 <see cref="ushort"/> 错误码，0 表示成功；
/// - 读 IO 类函数（<c>dmc_read_inbit</c>/<c>dmc_read_outbit</c>/<c>dmc_read_inport</c>/<c>dmc_read_outport</c>）
///   直接返回点位/端口值（0/1 或 16 位端口值），无错误码（现场若手册不同，仅在本文件与
///   <see cref="AutoTaping.Hardware.Io.MotionCardIoService"/> 内修正，不扩散到业务层）。
/// </summary>
internal static class LeadshineApi
{
    private const string Dll = "LTDMC.dll";

    // ---------- 板卡 ----------

    [DllImport(Dll)] internal static extern ushort dmc_board_init();
    [DllImport(Dll)] internal static extern ushort dmc_board_close();
    [DllImport(Dll)] internal static extern ushort dmc_board_reset();

    // ---------- 轴参数 ----------

    [DllImport(Dll)] internal static extern ushort dmc_set_pulse_outmode(ushort card, ushort axis, ushort outmode);
    [DllImport(Dll)] internal static extern ushort dmc_set_equiv(ushort card, ushort axis, double equiv);
    [DllImport(Dll)] internal static extern ushort dmc_set_profile_unit(
        ushort card, ushort axis, double minVel, double maxVel, double tAcc, double tDec);
    [DllImport(Dll)] internal static extern ushort dmc_set_dec_stop_time(ushort card, ushort axis, double stopTime);

    // ---------- 回零 ----------

    [DllImport(Dll)] internal static extern ushort dmc_set_home_profile_unit(
        ushort card, ushort axis, double homeVel, double homeAcc, ushort homeMode);
    [DllImport(Dll)] internal static extern ushort dmc_home_move(ushort card, ushort axis);
    [DllImport(Dll)] internal static extern ushort dmc_get_home_result(ushort card, ushort axis, out ushort stopFlag);

    // ---------- 限位信号逻辑 ----------

    [DllImport(Dll)] internal static extern ushort dmc_set_elogic(ushort card, ushort axis, ushort elogic);
    [DllImport(Dll)] internal static extern ushort dmc_set_einlogic(ushort card, ushort axis, ushort einlogic);

    // ---------- 运动 ----------

    /// <summary>定位运动。posiMode：0=相对，1=绝对。</summary>
    [DllImport(Dll)] internal static extern ushort dmc_pmove_unit(
        ushort card, ushort axis, double dist, ushort posiMode);
    [DllImport(Dll)] internal static extern ushort dmc_vmove(ushort card, ushort axis, ushort dir);
    /// <summary>停止。stopMode：0=减速停，1=急停。</summary>
    [DllImport(Dll)] internal static extern ushort dmc_stop(ushort card, ushort axis, ushort stopMode);
    [DllImport(Dll)] internal static extern ushort dmc_emg_stop(ushort card);

    // ---------- 状态 ----------

    /// <summary>到位查询。0=运动中，1=到位。</summary>
    [DllImport(Dll)] internal static extern ushort dmc_check_done(ushort card, ushort axis);
    [DllImport(Dll)] internal static extern ushort dmc_get_position_unit(ushort card, ushort axis, out double pos);
    [DllImport(Dll)] internal static extern ushort dmc_get_stop_reason(ushort card, ushort axis, out ushort reason);

    // ---------- 软限位 ----------

    [DllImport(Dll)] internal static extern ushort dmc_set_softlimit_unit(
        ushort card, ushort axis, ushort enable, ushort sourceSel, double positive, double negative);

    // ---------- IO ----------

    [DllImport(Dll)] internal static extern ushort dmc_read_inbit(ushort card, ushort bitNo);
    [DllImport(Dll)] internal static extern ushort dmc_read_outbit(ushort card, ushort bitNo);
    [DllImport(Dll)] internal static extern ushort dmc_write_outbit(ushort card, ushort bitNo, ushort onOff);
    [DllImport(Dll)] internal static extern ushort dmc_read_inport(ushort card, ushort portNo);
    [DllImport(Dll)] internal static extern ushort dmc_read_outport(ushort card, ushort portNo);
    [DllImport(Dll)] internal static extern ushort dmc_write_outport(ushort card, ushort portNo, ushort value);
    [DllImport(Dll)] internal static extern ushort dmc_set_doelogic(ushort card, ushort bitNo, ushort logic);
    [DllImport(Dll)] internal static extern ushort dmc_set_inbit_filter(ushort card, ushort bitNo, ushort filterMs);
}
