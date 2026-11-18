// 职责：海康相机冒烟测试——用真实的 HikvisionCamera 打开相机、软触发取一帧、存一张缩略 BMP
// 层：tools（独立控制台程序，不参与主解决方案构建）
// 依赖：AutoTaping.Hardware（HAS_MVS 生效时才是真实实现）、MVS 原生运行时

using AutoTaping.Hardware.Camera;

Console.WriteLine($"ProcessBitness = {(Environment.Is64BitProcess ? "x64" : "x86")}");
Console.WriteLine("提示：MVS 客户端会独占相机，先关掉它再跑本工具，否则打开会返回 0x80000203。");

var options = new HikvisionCameraOptions
{
    SerialNumber = "",          // 空 = 取枚举到的第一台
    ExposureTimeUs = 20000,
    GainDb = 0,
    TriggerMode = "Software",
    TimeoutMs = 3000,
};

using var camera = new HikvisionCamera(options, new ConsoleLogger<HikvisionCamera>());

Console.WriteLine($"Initialize = {camera.Initialize()}, IsConnected = {camera.IsConnected}");
if (!camera.IsConnected)
{
    return 1;
}

Console.WriteLine($"StartGrabbing = {camera.StartGrabbing()}");
Console.WriteLine($"SetExposure(8000) = {camera.SetExposure(8000)}");

var frame = await camera.TriggerAndGrabAsync(3000, CancellationToken.None);
Console.WriteLine($"Grab: Success={frame.Success}, {frame.Width}x{frame.Height}, len={frame.Mono8Data?.Length}, err={frame.ErrorMessage}");

if (!frame.Success || frame.Mono8Data is not { } data || frame.Width <= 0 || frame.Height <= 0)
{
    camera.StopGrabbing();
    return 1;
}

long sum = 0;
byte min = 255, max = 0;
foreach (var b in data)
{
    sum += b;
    if (b < min) min = b;
    if (b > max) max = b;
}

Console.WriteLine($"像素: min={min} max={max} mean={(double)sum / (frame.Width * frame.Height):F1}");

// 1/2 下采样存 24 位 BMP，方便直接用图片查看器确认画面
var width = frame.Width / 2;
var height = frame.Height / 2;
var stride = ((width * 3) + 3) / 4 * 4;
var pixels = new byte[stride * height];
for (var y = 0; y < height; y++)
{
    for (var x = 0; x < width; x++)
    {
        var gray = data[(y * 2 * frame.Width) + (x * 2)];
        var offset = (y * stride) + (x * 3);
        pixels[offset] = gray;
        pixels[offset + 1] = gray;
        pixels[offset + 2] = gray;
    }
}

var path = Path.Combine(AppContext.BaseDirectory, "frame.bmp");
using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
using (var writer = new BinaryWriter(fs))
{
    writer.Write((ushort)0x4D42);
    writer.Write(54 + pixels.Length);
    writer.Write(0);
    writer.Write(54);
    writer.Write(40);
    writer.Write(width);
    writer.Write(height);
    writer.Write((ushort)1);
    writer.Write((ushort)24);
    writer.Write(0);
    writer.Write(pixels.Length);
    writer.Write(2835);
    writer.Write(2835);
    writer.Write(0);
    writer.Write(0);
    writer.Write(pixels);
}

Console.WriteLine($"已保存 {path}（{width}x{height}）");

camera.StopGrabbing();
return 0;

// 极简控制台 logger：把 HikvisionCamera 内部的日志直接打出来
internal sealed class ConsoleLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                            TState state, Exception? exception,
                            Func<TState, Exception?, string> formatter)
    {
        Console.WriteLine($"  [{logLevel}] {formatter(state, exception)}");
        if (exception is not null)
        {
            Console.WriteLine("   " + exception.Message);
        }
    }
}
