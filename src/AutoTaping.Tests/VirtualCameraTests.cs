// 职责：VirtualCamera 单元测试（连续取 10 帧 MARK 偏移在 ±5px 内且随机 / 固定偏移注入），对应 S2 验收标准第 5 条
// 层：Tests
// 依赖：AutoTaping.Hardware.Camera

using AutoTaping.Hardware.Camera;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTaping.Tests;

/// <summary>
/// <see cref="VirtualCamera"/> 的行为测试：合成图能出帧、每帧 MARK 偏移在 ±5px 内且随机、支持固定偏移注入。
/// </summary>
public sealed class VirtualCameraTests
{
    [Fact]
    public async Task VirtualCamera_InjectsRandomOffsetWithinRange()
    {
        using var camera = new VirtualCamera(NullLogger<VirtualCamera>.Instance);
        Assert.True(camera.Initialize());
        Assert.True(camera.StartGrabbing());

        var offsets = new List<(int X, int Y)>();
        for (var i = 0; i < 10; i++)
        {
            var frame = await camera.TriggerAndGrabAsync(500, CancellationToken.None);
            Assert.True(frame.Success);
            Assert.NotNull(frame.Mono8Data);
            Assert.Equal(1280, frame.Width);
            Assert.Equal(1024, frame.Height);
            offsets.Add((camera.LastOffsetX, camera.LastOffsetY));
        }

        Assert.All(offsets, o => Assert.InRange(o.X, -5, 5));
        Assert.All(offsets, o => Assert.InRange(o.Y, -5, 5));

        // 随机性：10 帧不全是同一偏移
        Assert.True(offsets.Distinct().Count() > 1);
    }

    [Fact]
    public async Task VirtualCamera_UsesInjectedFixedOffset()
    {
        using var camera = new VirtualCamera(NullLogger<VirtualCamera>.Instance);
        camera.Initialize();
        camera.StartGrabbing();

        camera.InjectOffset(3, -3);
        var frame = await camera.TriggerAndGrabAsync(500, CancellationToken.None);

        Assert.True(frame.Success);
        Assert.Equal(3, camera.LastOffsetX);
        Assert.Equal(-3, camera.LastOffsetY);
    }
}
