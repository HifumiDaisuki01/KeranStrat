namespace KeranStrat.Core;

public interface IReplayEngine
{
    bool Start(int slot);
    void Stop(int slot);
    bool IsPlaying(int slot);
    int Cursor(int slot);
    bool IsAlive(int slot);
    float? PositionError(int slot, int cursor) => null;
}

public sealed class ReplayEntry(int role, int slot, int offsetTicks, int totalTicks)
{
    public int Role { get; } = role;
    public int Slot { get; } = slot;
    public int OffsetTicks { get; } = offsetTicks;
    public int TotalTicks { get; } = totalTicks;
    public int? StartedTick { get; internal set; }
    public int MaxCursorDrift { get; internal set; }
    public bool Finished { get; internal set; }
    public int PositionSamples { get; internal set; }
    public float MaxPositionError { get; internal set; }
}

// All starts are issued by the same game-thread OnTick callback, never by per-bot timers.
public sealed class ReplayCoordinator(IReplayEngine engine)
{
    private ReplayEntry[] _entries = [];
    public IReadOnlyList<ReplayEntry> Entries => _entries;
    public bool Active { get; private set; }
    public int StartTick { get; private set; }
    public static int ToTicks(int milliseconds, double tickInterval)
    {
        if (milliseconds < 0 || milliseconds > 120000 || !double.IsFinite(tickInterval) || tickInterval <= 0)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        return checked((int)Math.Ceiling(milliseconds / (tickInterval * 1000)));
    }
    public void Begin(int startTick, ReplayEntry[] entries)
    {
        if (Active || entries.Length is < 1 or > 5 || entries.Select(e => e.Slot).Distinct().Count() != entries.Length ||
            entries.Any(e => e.OffsetTicks < 0 || e.TotalTicks <= 0)) throw new InvalidOperationException("回放状态无效。");
        foreach (var e in entries) { e.StartedTick = null; e.Finished = false; e.MaxCursorDrift = 0; e.PositionSamples = 0; e.MaxPositionError = 0; }
        _entries = entries; StartTick = startTick; Active = true;
    }
    public void Tick(int now)
    {
        if (!Active || now < StartTick) return;
        try
        {
            bool allDone = true;
            foreach (var entry in _entries)
            {
                if (entry.Finished) continue;
                if (!engine.IsAlive(entry.Slot)) throw new InvalidOperationException($"队员 {entry.Role} 的 BOT 已死亡或断开。");
                int due = checked(StartTick + entry.OffsetTicks);
                if (entry.StartedTick == null && now >= due)
                {
                    if (!engine.Start(entry.Slot)) throw new InvalidOperationException($"队员 {entry.Role} 无法开始回放。");
                    entry.StartedTick = now;
                }
                if (entry.StartedTick is int started)
                {
                    int cursor = engine.Cursor(entry.Slot);
                    // Native cursor advances on usercmds, not on a C# timer. Log rather than hide drift.
                    if (cursor >= 0) entry.MaxCursorDrift = Math.Max(entry.MaxCursorDrift, Math.Abs(cursor - (now - started)));
                    if (engine.PositionError(entry.Slot, cursor) is float error)
                    { entry.PositionSamples++; entry.MaxPositionError = Math.Max(entry.MaxPositionError, error); }
                    if (now > started && !engine.IsPlaying(entry.Slot))
                    {
                        if (cursor >= 0 && cursor < entry.TotalTicks - 1)
                            throw new InvalidOperationException($"队员 {entry.Role} 提前停止：{cursor}/{entry.TotalTicks}。");
                        entry.Finished = true;
                    }
                    if (now - started > entry.TotalTicks + 128) throw new TimeoutException($"队员 {entry.Role} 回放超时。");
                }
                if (!entry.Finished) allDone = false;
            }
            if (allDone) { Active = false; foreach (var e in _entries) engine.Stop(e.Slot); }
        }
        catch { Cancel(); throw; }
    }
    public void Cancel()
    {
        Active = false;
        foreach (var e in _entries) { try { engine.Stop(e.Slot); } catch { /* release every remaining bot */ } }
    }
}
