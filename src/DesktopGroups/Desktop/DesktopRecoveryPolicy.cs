namespace DesktopGroups;

// Coalesce notification bursts without letting a busy desktop starve recovery.
// Once an attach fails, notifications must not bypass or reset its retry backoff.
internal sealed class DesktopRecoveryPolicy
{
    private DateTimeOffset due;
    private DateTimeOffset? burstStarted;
    private int failures;
    public string Reason { get; private set; } = "启动";
    public bool Pending { get; private set; } = true;
    public void Signal(string reason, DateTimeOffset now)
    {
        Reason = reason;
        if (Pending && failures > 0) return;
        Pending = true;
        burstStarted ??= now;
        var quietBoundary = now.AddMilliseconds(300);
        var latestBoundary = burstStarted.Value.AddSeconds(2);
        due = quietBoundary < latestBoundary ? quietBoundary : latestBoundary;
    }
    public bool IsDue(DateTimeOffset now) => Pending && now >= due;
    public void Complete(bool success, DateTimeOffset now)
    {
        burstStarted = null;
        if (success) { Pending = false; failures = 0; return; }
        Pending = true;
        due = now.AddSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures++, 5))));
    }
}
