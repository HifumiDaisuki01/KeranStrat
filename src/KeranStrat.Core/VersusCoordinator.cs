namespace KeranStrat.Core;

public interface IVersusReplayEngine : IDefenseReplayEngine { bool Hold(int slot); }
public enum VersusPhase { Waiting, Replaying, Holding, CombatAI, Dead }
public sealed class VersusEntry(string key, int slot, int offset, int frames, int handoff, bool automatic)
{
    public string Key { get; } = key;
    public int Slot { get; } = slot;
    public int Offset { get; } = offset;
    public int Frames { get; } = frames;
    public int Handoff { get; } = handoff;
    public bool Automatic { get; } = automatic;
    public VersusPhase Phase { get; internal set; }
    public int? Started { get; internal set; }
    public int? HandedOff { get; internal set; }
}
public sealed class VersusCoordinator(IVersusReplayEngine engine)
{
    private readonly HashSet<int> _dead = [];
    public IReadOnlyList<VersusEntry> Entries { get; private set; } = [];
    public int StartTick { get; private set; }
    public bool Hybrid { get; private set; }
    public bool Active { get; private set; }
    private int _grace;
    public void Begin(int start, VersusEntry[] entries, bool hybrid, double interval)
    {
        if (Active || entries.Length is < 2 or > 10 || entries.Select(e => e.Key).Distinct().Count() != entries.Length ||
            entries.Select(e => e.Slot).Distinct().Count() != entries.Length || !double.IsFinite(interval) || interval <= 0)
            throw new InvalidOperationException("KSV 编组无效。");
        foreach (var e in entries)
        {
            VersusScene.Parse(e.Key);
            if (e.Offset < 0 || e.Frames < 1 || e.Handoff < 0 || e.Handoff > e.Frames) throw new InvalidDataException("KSV 轨迹时间无效。");
            e.Phase = VersusPhase.Waiting; e.Started = null; e.HandedOff = null;
        }
        if (!entries.Any(e => VersusScene.Parse(e.Key).Team == 2) || !entries.Any(e => VersusScene.Parse(e.Key).Team == 3))
            throw new InvalidOperationException("KSV 双方必须都有角色。");
        Entries = entries; StartTick = start; Hybrid = hybrid; _grace = (int)Math.Ceiling(2 / interval); _dead.Clear(); Active = true;
    }
    public void MarkDead(int slot) { if (Active) _dead.Add(slot); }
    public void Tick(int now)
    {
        if (!Active) return;
        foreach (var e in Entries)
        {
            if (e.Phase == VersusPhase.Dead) continue;
            if (_dead.Contains(e.Slot)) { engine.Stop(e.Slot); e.Phase = VersusPhase.Dead; continue; }
            if (!engine.IsConnected(e.Slot)) throw new IOException(e.Key + " BOT 意外断线，本轮取消。");
            if (!engine.IsAlive(e.Slot)) { engine.Stop(e.Slot); e.Phase = VersusPhase.Dead; continue; }
            if (e.Phase == VersusPhase.Waiting && now >= StartTick + e.Offset)
            {
                if (!engine.Start(e.Slot)) throw new InvalidOperationException(e.Key + " 原生回放启动失败。");
                e.Started = now; e.Phase = VersusPhase.Replaying;
            }
            if (e.Phase != VersusPhase.Replaying || e.Started is not int started) continue;
            int elapsed = now - started;
            bool playing = engine.IsPlaying(e.Slot);
            int end = Hybrid ? e.Handoff : e.Frames;
            if (!playing && elapsed < end - 2) throw new IOException(e.Key + " 原生回放提前中断。");
            if (elapsed > e.Frames + _grace && playing) throw new TimeoutException(e.Key + " 回放结束超时。");
            if (elapsed < end || (Hybrid && !e.Automatic ? false : playing)) continue;
            if (Hybrid)
            {
                if (!engine.HandoffToCombatAI(e.Slot)) throw new InvalidOperationException(e.Key + " AI 交接失败。");
                e.Phase = VersusPhase.CombatAI; e.HandedOff = now;
            }
            else
            {
                if (!engine.Hold(e.Slot)) throw new InvalidOperationException(e.Key + " 严格回放锁定失败。");
                e.Phase = VersusPhase.Holding;
            }
        }
    }
    public int Alive(int team) => Entries.Count(e => VersusScene.Parse(e.Key).Team == team && e.Phase != VersusPhase.Dead);
    public string? Result(bool timedOut) => Alive(2) == 0 && Alive(3) == 0 ? "Draw" : Alive(2) == 0 ? "CT" : Alive(3) == 0 ? "T" : timedOut ? "Timeout" : null;
    public void Cancel()
    {
        Active = false;
        foreach (var e in Entries) try { engine.Stop(e.Slot); } catch { /* Release remaining identities too. */ }
    }
}
