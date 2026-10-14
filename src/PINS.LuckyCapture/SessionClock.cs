namespace PINS.LuckyCapture;

/// <summary>Host arrival times anchored once to UTC; later wall-clock adjustments cannot reorder frames.</summary>
public sealed class SessionClock {
    private readonly TimeProvider clock;
    private readonly DateTimeOffset anchor;
    private readonly long started;
    public SessionClock(TimeProvider? clock = null) {
        this.clock = clock ?? TimeProvider.System;
        started = this.clock.GetTimestamp();
        anchor = this.clock.GetUtcNow();
    }
    public DateTimeOffset Now => anchor + clock.GetElapsedTime(started, clock.GetTimestamp());
}
