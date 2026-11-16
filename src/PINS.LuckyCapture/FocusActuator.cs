using NINA.Equipment.Interfaces.Mediator;

namespace PINS.LuckyCapture;

public interface IFocusActuator {
    FocusDevice Read();
    Task MoveAsync(int position, bool relative, CancellationToken token);
}

public sealed class ConnectedFocusActuator(IFocuserMediator mediator) : IFocusActuator {
    public FocusDevice Read() {
        var info = mediator.GetInfo();
        return new(info?.Connected == true, info?.Name ?? "PINS focuser", info?.Position ?? -1,
            info?.IsMoving == true, info?.IsSettling == true, info?.MaxStep > 0 ? info.MaxStep : null, true);
    }

    public async Task MoveAsync(int position, bool relative, CancellationToken token) {
        var before = Read();
        if (!before.Connected) throw new LuckyCaptureException("FOCUSER_NOT_CONNECTED", "Connect a focuser in PINS Equipment");
        if (before.Moving || before.Settling) throw new LuckyCaptureException("FOCUSER_BUSY", "PINS focuser is already moving or settling");
        var expected = relative ? checked(before.Position + position) : position;
        var actual = relative ? await mediator.MoveFocuserRelative(position, token).ConfigureAwait(false) :
            await mediator.MoveFocuser(position, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var after = Read();
        if (!after.Connected) throw new LuckyCaptureException("FOCUSER_NOT_CONNECTED", "PINS focuser disconnected during movement");
        if (actual != expected || after.Position != expected || after.Moving || after.Settling)
            throw new LuckyCaptureException("FOCUSER_POSITION_MISMATCH", "Focuser did not reach a stable requested position");
    }
}

public sealed class VirtualFocusActuator : IFocusActuator {
    private int position = 25000;
    private int moving;
    private string fault = "none";
    public void SetFault(string value) => Volatile.Write(ref fault, value);
    public FocusDevice Read() => new(Volatile.Read(ref fault) != "disconnect", "Virtual focuser", Volatile.Read(ref position), moving != 0, false, 50000, false);

    public async Task MoveAsync(int target, bool relative, CancellationToken token) {
        if (!Read().Connected) throw new LuckyCaptureException("FOCUSER_NOT_CONNECTED", "Virtual disconnect injection is active");
        if (Interlocked.CompareExchange(ref moving, 1, 0) != 0)
            throw new LuckyCaptureException("FOCUSER_BUSY", "Virtual focuser is moving");
        try {
            if (fault == "stall") await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            if (relative) target = checked(position + target);
            if (target is < 0 or > 50000) throw new LuckyCaptureException("FOCUSER_LIMIT", "Virtual travel is 0 to 50000");
            while (position != target) {
                await Task.Delay(10, token).ConfigureAwait(false);
                Volatile.Write(ref position, position + Math.Clamp(target - position, -100, 100));
            }
            await Task.Delay(30, token).ConfigureAwait(false);
            if (fault == "readback") throw new LuckyCaptureException("FOCUSER_POSITION_MISMATCH", "Virtual readback fault injection");
        } finally { Volatile.Write(ref moving, 0); }
    }
}
