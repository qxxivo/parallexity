namespace PhoneRemote;

// A held mouse button belongs to one gesture on one authenticated phone.
// A short lease releases it even if the browser closes without sending pointerup.
internal sealed class DragLease
{
    private readonly IDesktopControls controls;
    private readonly Func<string, bool> permitted;
    private readonly Func<DateTimeOffset> now;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? owner, gesture;
    private DateTimeOffset expires;
    private bool disposed;
    internal DragLease(IDesktopControls controls, Func<string, bool> permitted, Func<DateTimeOffset>? now = null)
    { this.controls = controls; this.permitted = permitted; this.now = now ?? (() => DateTimeOffset.UtcNow); }

    internal async Task<bool> ExecuteAsync(RemoteCommand command, string phone)
    {
        await gate.WaitAsync();
        try
        {
            await ExpireHeldAsync();
            if (disposed || !permitted(phone)) return false;
            if (command.Action == "drag-start")
            {
                if (owner is not null) return owner == phone && gesture == command.Drag;
                if (!await controls.ExecuteAsync(command)) return false;
                owner = phone; gesture = command.Drag; expires = now().AddMilliseconds(900);
                return true;
            }
            if (command.Action is "drag-move" or "drag-end")
            {
                if (owner != phone || gesture != command.Drag) return command.Action == "drag-end" && owner is null;
                if (command.Action == "drag-end") { await ReleaseHeldAsync(); return true; }
                expires = now().AddMilliseconds(900);
                if (await controls.ExecuteAsync(command)) return true;
                await ReleaseHeldAsync(); return false;
            }
            if (owner is not null) return false;
            return await controls.ExecuteAsync(command);
        }
        finally { gate.Release(); }
    }
    internal async Task ExpireAsync()
    {
        await gate.WaitAsync();
        try { await ExpireHeldAsync(); } finally { gate.Release(); }
    }
    private async Task ExpireHeldAsync()
    {
        if (owner is not null && (now() >= expires || !permitted(owner))) await ReleaseHeldAsync();
    }
    private async Task ReleaseHeldAsync()
    {
        var previous = gesture; owner = gesture = null;
        if (previous is not null) await controls.ExecuteAsync(new("drag-end", Drag: previous));
    }
    internal async Task StopAsync()
    {
        await gate.WaitAsync();
        try { disposed = true; await ReleaseHeldAsync(); } finally { gate.Release(); }
    }
}
