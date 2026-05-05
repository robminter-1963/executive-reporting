namespace TleReportingDashboard.Web.Services;

// Per-circuit (per-user) flag indicating that the current Blazor session is
// inside an admin editor page. While active, ConfigDbCache bypasses cache
// reads and evicts the matching keys, so the editor always sees the latest
// committed state of RPT_* tables — no stale "I just saved this" surprises.
//
// Pages mark their lifetime with `using var _ = EditorMode.Enter();` in
// OnInitializedAsync so the flag clears on dispose / navigation. The depth
// counter handles nested or overlapping editor pages within the same circuit
// (rare in practice, but cheap to support).
public sealed class EditorModeState
{
    private int _depth;

    public bool IsActive => Volatile.Read(ref _depth) > 0;

    public IDisposable Enter()
    {
        Interlocked.Increment(ref _depth);
        return new Scope(this);
    }

    private sealed class Scope : IDisposable
    {
        private EditorModeState? _state;
        public Scope(EditorModeState s) => _state = s;
        public void Dispose()
        {
            var s = Interlocked.Exchange(ref _state, null);
            if (s is not null) Interlocked.Decrement(ref s._depth);
        }
    }
}
