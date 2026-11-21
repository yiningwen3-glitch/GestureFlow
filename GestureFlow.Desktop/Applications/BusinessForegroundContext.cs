namespace GestureFlow.Desktop.Applications;

internal readonly record struct ForegroundWindowIdentity(nint Handle, int ProcessId)
{
    public bool Matches(RunningApplication? application) => application is not null && Handle != 0
        && application.WindowHandle == Handle && application.WindowOwnerProcessId == ProcessId;
}

// Used by presentation and saved profile selection. Input authorization still inspects the real foreground.
internal sealed class BusinessForegroundContext(int ownProcessId)
{
    public RunningApplication? Target { get; private set; }

    public bool Observe(RunningApplication? observed, ForegroundWindowIdentity current, Func<RunningApplication, bool> isLive)
    {
        var next = current.ProcessId == ownProcessId || current.Matches(Target) ? Target : null;
        if (current.ProcessId != ownProcessId && current.Matches(observed))
            next = observed;
        if (next is not null && !isLive(next))
            next = null;
        if (next == Target)
            return false;
        Target = next;
        return true;
    }

    public RunningApplication? ForProfileSelection(ForegroundWindowIdentity current, Func<RunningApplication, bool> isLive) =>
        Target is { } target && (current.ProcessId == ownProcessId || current.Matches(target)) && isLive(target)
            ? target : null;
}
