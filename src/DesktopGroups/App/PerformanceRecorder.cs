using System.Diagnostics;

namespace DesktopGroups;

internal sealed record PerformanceSample(DateTimeOffset At, double ProcessCpuPercent, double WorkingSetMB,
    double PrivateMB, double UiDelayMs, bool ManagerVisible, bool Interacting);

internal sealed class PerformanceRecorder
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Queue<PerformanceSample> samples = new();
    private TimeSpan previousCpu;
    private double previousTime;
    public double? ReadyMs { get; private set; }
    public int SampleCount => samples.Count;
    public PerformanceSample? Latest => samples.LastOrDefault();
    public PerformanceRecorder()
    {
        using var process = Process.GetCurrentProcess();
        previousCpu = process.TotalProcessorTime; previousTime = clock.Elapsed.TotalMilliseconds;
    }
    public void MarkReady() => ReadyMs ??= clock.Elapsed.TotalMilliseconds;
    public PerformanceSample Sample(double delay, bool visible, bool interacting)
    {
        using var process = Process.GetCurrentProcess();
        var now = clock.Elapsed.TotalMilliseconds;
        var cpu = process.TotalProcessorTime;
        var percent = previousTime == 0 ? 0 : Math.Clamp((cpu - previousCpu).TotalMilliseconds /
            Math.Max(1, now - previousTime) / Environment.ProcessorCount * 100, 0, 100);
        previousCpu = cpu; previousTime = now;
        var sample = new PerformanceSample(DateTimeOffset.Now, percent, process.WorkingSet64 / 1048576d,
            process.PrivateMemorySize64 / 1048576d, delay, visible, interacting);
        samples.Enqueue(sample); while (samples.Count > 240) samples.Dequeue();
        return sample;
    }
    public object Snapshot(object icons, object panels) => new { Version = AppBrand.Version, CapturedAt = DateTimeOffset.Now,
        ReadyMs, StartupScope = "管理窗口构造至抽屉首次就绪（不包含进程启动前的系统开销）",
        CpuDefinition = "进程CPU时间增量/采样墙钟时间/逻辑处理器数，0-100%", MaxSamples = 240,
        Samples = samples.ToArray(), Icons = icons, Panels = panels };
}
