namespace DesktopGroups;

// Repeated desktop notifications are coalesced. Failed attaches keep retrying with a bounded backoff.
internal sealed class DesktopRecoveryPolicy
{
    private DateTimeOffset due;
    private int failures;
    public string Reason { get; private set; } = "启动";
    public bool Pending { get; private set; } = true;
    public void Signal(string reason, DateTimeOffset now)
    { Reason = reason; Pending = true; failures = 0; due = now.AddMilliseconds(300); }
    public bool IsDue(DateTimeOffset now) => Pending && now >= due;
    public void Complete(bool success, DateTimeOffset now)
    {
        if (success) { Pending = false; failures = 0; return; }
        Pending = true;
        due = now.AddSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures++, 5))));
    }
}
