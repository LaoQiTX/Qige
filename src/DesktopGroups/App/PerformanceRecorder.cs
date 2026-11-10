using System.Diagnostics;

namespace DesktopGroups;

internal sealed record PerformanceSample(DateTimeOffset At, double ProcessCpuPercent, double WorkingSetMB,
    double PrivateMB, double UiDelayMs, bool ManagerVisible, bool Interacting);

internal sealed class PerformanceRecorder
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Queue<PerformanceSample> samples = new();
    private readonly DateTimeOffset startedAt = DateTimeOffset.Now;
    private readonly TimeSpan initialCpu;
    private readonly double initialTime, initialWorkingSetMB, initialPrivateMB;
    private TimeSpan previousCpu;
    private double previousTime;
    private double peakWorkingSetMB, peakPrivateMB, averageCpuPercent;
    private long totalSamples;
    public double? ReadyMs { get; private set; }
    public int SampleCount => samples.Count;
    public PerformanceSample? Latest => samples.LastOrDefault();
    public PerformanceRecorder()
    {
        using var process = Process.GetCurrentProcess();
        initialCpu = previousCpu = process.TotalProcessorTime;
        initialTime = previousTime = clock.Elapsed.TotalMilliseconds;
        initialWorkingSetMB = peakWorkingSetMB = process.WorkingSet64 / 1048576d;
        initialPrivateMB = peakPrivateMB = process.PrivateMemorySize64 / 1048576d;
    }
    public void MarkReady() => ReadyMs ??= clock.Elapsed.TotalMilliseconds;
    public PerformanceSample Sample(double delay, bool visible, bool interacting)
    {
        using var process = Process.GetCurrentProcess();
        var now = clock.Elapsed.TotalMilliseconds;
        var cpu = process.TotalProcessorTime;
        var percent = Math.Clamp((cpu - previousCpu).TotalMilliseconds /
            Math.Max(1, now - previousTime) / Environment.ProcessorCount * 100, 0, 100);
        averageCpuPercent = Math.Clamp((cpu - initialCpu).TotalMilliseconds /
            Math.Max(1, now - initialTime) / Environment.ProcessorCount * 100, 0, 100);
        previousCpu = cpu; previousTime = now;
        var sample = new PerformanceSample(DateTimeOffset.Now, percent, process.WorkingSet64 / 1048576d,
            process.PrivateMemorySize64 / 1048576d, delay, visible, interacting);
        totalSamples++;
        peakWorkingSetMB = Math.Max(peakWorkingSetMB, sample.WorkingSetMB);
        peakPrivateMB = Math.Max(peakPrivateMB, sample.PrivateMB);
        samples.Enqueue(sample); while (samples.Count > 240) samples.Dequeue();
        return sample;
    }
    public object Snapshot(object icons, object panels) => new { Version = AppBrand.Version, CapturedAt = DateTimeOffset.Now,
        ReadyMs, StartupScope = "管理窗口构造至抽屉首次就绪（不包含进程启动前的系统开销）",
        CpuDefinition = "进程CPU时间增量/采样墙钟时间/逻辑处理器数，0-100%", MaxSamples = 240,
        Session = new
        {
            StartedAt = startedAt, UptimeSeconds = clock.Elapsed.TotalSeconds, TotalSamples = totalSamples,
            ObservedSeconds = (previousTime - initialTime) / 1000,
            AverageCpuPercent = averageCpuPercent,
            InitialWorkingSetMB = initialWorkingSetMB, InitialPrivateMB = initialPrivateMB,
            PeakWorkingSetMB = peakWorkingSetMB, PeakPrivateMB = peakPrivateMB,
            WorkingSetChangeMB = (Latest?.WorkingSetMB ?? initialWorkingSetMB) - initialWorkingSetMB,
            PrivateMemoryChangeMB = (Latest?.PrivateMB ?? initialPrivateMB) - initialPrivateMB,
            Scope = "本次进程内的采样统计，最近240个明细滚动保留；峰值为采样观测值，内存差值包含正常缓存和界面加载"
        },
        Samples = samples.ToArray(), Icons = icons, Panels = panels };
}
