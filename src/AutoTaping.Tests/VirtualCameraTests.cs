// 职责：VirtualCamera 单元测试（样本图优先 / 合成图回退 / 平台位置模拟）
// 层：Tests
// 依赖：AutoTaping.Hardware.Camera
//
// 说明：偏移注入已移除，改为「样本图提供者」——每次取图随机返回一张模拟采样图；
//       九点标定期间走平台位置模拟，必须回退合成图，故单独覆盖。

using AutoTaping.Hardware.Camera;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTaping.Tests;

/// <summary>
/// <see cref="VirtualCamera"/> 的行为测试：无样本图时用合成图出帧、有样本图时优先返回样本图、
/// 平台位置模拟下必须回退合成图（九点标定要求像素与平台位置确定性对应）。
/// </summary>
public sealed class VirtualCameraTests
{
    [Fact]
    public async Task VirtualCamera_GeneratesSyntheticFrames_WhenNoSampleProvider()
    {
        using var camera = new VirtualCamera(NullLogger<VirtualCamera>.Instance);
        Assert.True(camera.Initialize());
        Assert.True(camera.StartGrabbing());

        for (var i = 0; i < 3; i++)
        {
            var frame = await camera.TriggerAndGrabAsync(500, CancellationToken.None);

            Assert.True(frame.Success);
            Assert.NotNull(frame.Mono8Data);
            Assert.Equal(1280, frame.Width);
            Assert.Equal(1024, frame.Height);
            Assert.Equal(1280 * 1024, frame.Mono8Data.Length);

            // 必须画出了 MARK（存在亮像素），否则视觉识别无从谈起
            Assert.Contains(frame.Mono8Data, value => value > 200);
        }
    }

    [Fact]
    public async Task VirtualCamera_PrefersSampleProvider()
    {
        var sample = new byte[] { 10, 20, 30, 40, 50, 60 };
        using var camera = new VirtualCamera(
            NullLogger<VirtualCamera>.Instance, () => (sample, 3, 2));

        camera.Initialize();
        camera.StartGrabbing();

        var frame = await camera.TriggerAndGrabAsync(500, CancellationToken.None);

        Assert.True(frame.Success);
        Assert.Equal(3, frame.Width);
        Assert.Equal(2, frame.Height);
        Assert.Same(sample, frame.Mono8Data);
    }

    [Fact]
    public async Task VirtualCamera_FallsBackToSynthetic_WhenPlatformPositionSet()
    {
        using var camera = new VirtualCamera(
            NullLogger<VirtualCamera>.Instance, () => (new byte[6], 3, 2));

        camera.Initialize();
        camera.StartGrabbing();
        camera.SetPlatformPosition(150, 200);

        var frame = await camera.TriggerAndGrabAsync(500, CancellationToken.None);

        Assert.True(frame.Success);
        Assert.Equal(1280, frame.Width);
        Assert.Equal(1024, frame.Height);
    }

    [Fact]
    public async Task VirtualCamera_MovesMarkWithPlatformPosition()
    {
        using var camera = new VirtualCamera(NullLogger<VirtualCamera>.Instance);
        camera.Initialize();
        camera.StartGrabbing();

        camera.SetPlatformPosition(150, 200);
        var origin = await camera.TriggerAndGrabAsync(500, CancellationToken.None);

        // 平台 X +10mm，模拟比例 4px/mm → MARK 右移约 40px
        camera.SetPlatformPosition(160, 200);
        var shifted = await camera.TriggerAndGrabAsync(500, CancellationToken.None);

        var delta = CentroidX(shifted.Mono8Data!) - CentroidX(origin.Mono8Data!);
        Assert.InRange(delta, 39.0, 41.0);

        // 退出模拟后恢复基准位置
        camera.ClearPlatformPosition();
        var cleared = await camera.TriggerAndGrabAsync(500, CancellationToken.None);
        var back = CentroidX(cleared.Mono8Data!) - CentroidX(origin.Mono8Data!);
        Assert.InRange(back, -1.0, 1.0);
    }

    /// <summary>计算亮像素（MARK）的平均 X 坐标，用于验证平台位移换算。</summary>
    private static double CentroidX(byte[] mono8)
    {
        const int width = 1280;
        double sum = 0;
        var count = 0;

        for (var i = 0; i < mono8.Length; i++)
        {
            if (mono8[i] > 200)
            {
                sum += i % width;
                count++;
            }
        }

        return count == 0 ? 0 : sum / count;
    }
}
