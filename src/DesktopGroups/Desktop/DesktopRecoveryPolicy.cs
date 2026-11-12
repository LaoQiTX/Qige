namespace DesktopGroups;

// Coalesce notification bursts without letting a busy desktop starve recovery.
// Once an attach fails, notifications must not bypass or reset its retry backoff.
internal sealed class DesktopRecoveryPolicy
{
    private DateTimeOffset due;
    private DateTimeOffset? burstStarted;
    private int failures;
    private DateTimeOffset? startupRetryUntil;
    public string Reason { get; private set; } = "启动";
    public bool Pending { get; private set; } = true;
    public void Signal(string reason, DateTimeOffset now)
    {
        Reason = reason;
        if (Pending && (failures > 0 || startupRetryUntil != null)) return;
        Pending = true;
        burstStarted ??= now;
        var quietBoundary = now.AddMilliseconds(300);
        var latestBoundary = burstStarted.Value.AddSeconds(2);
        due = quietBoundary < latestBoundary ? quietBoundary : latestBoundary;
    }
    public bool IsDue(DateTimeOffset now) => Pending && now >= due;
    public void BeginStartup(DateTimeOffset now) => startupRetryUntil = now.AddSeconds(20);
    public void Complete(bool success, DateTimeOffset now)
    {
        burstStarted = null;
        if (success) { Pending = false; failures = 0; startupRetryUntil = null; return; }
        Pending = true;
        // Explorer is often still creating DefView at logon. Do not enter a
        // 16/30-second sleep while the desktop is about to become available.
        if (startupRetryUntil is { } until && now < until)
        {
            due = now.AddMilliseconds(300);
            return;
        }
        due = now.AddSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures++, 5))));
    }
}
