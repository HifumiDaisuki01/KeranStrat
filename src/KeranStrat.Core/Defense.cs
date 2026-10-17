namespace KeranStrat.Core;

public interface IDefenseReplayEngine : IReplayEngine
{
    bool IsConnected(int slot);
    bool HandoffToCombatAI(int slot);
}

public enum DefenseBotState { Waiting, Replaying, CombatAI, Dead }
public sealed class DefenseBotEntry(int role, int slot, int offsetTicks, int totalTicks, int handoffTicks, bool automatic = false)
{
    public int Role { get; } = role;
    public int Slot { get; } = slot;
    public int OffsetTicks { get; } = offsetTicks;
    public int TotalTicks { get; } = totalTicks;
    public int HandoffTicks { get; } = handoffTicks;
    public bool Automatic { get; } = automatic;
    public int? StartedTick { get; internal set; }
    public int? HandoffTick { get; internal set; }
    public DefenseBotState State { get; internal set; }
}

// Separate policy from strict replay: deaths are normal, AI handoff retains ownership.
public sealed class DefenseBotController(IDefenseReplayEngine engine)
{
    private readonly HashSet<int> _deathEvents = [];
    public void MarkDead(int slot) { if (Active && Entries.Any(e => e.Slot == slot)) _deathEvents.Add(slot); }
    public IReadOnlyList<DefenseBotEntry> Entries { get; private set; } = [];
    public int StartTick { get; private set; }
    public bool Active { get; private set; }
    public int DeadCount => Entries.Count(e => e.State == DefenseBotState.Dead);
    public void Begin(int startTick, DefenseBotEntry[] entries)
    {
        if (Active || entries.Length is < 1 or > 5 || entries.Select(e => e.Slot).Distinct().Count() != entries.Length ||
            entries.Select(e => e.Role).Distinct().Count() != entries.Length ||
            entries.Any(e => e.Role is < 1 or > 5 || e.OffsetTicks < 0 || e.TotalTicks < 1 || e.HandoffTicks < 0 || e.HandoffTicks > e.TotalTicks))
            throw new InvalidOperationException("Defense BOT 编组无效。");
        Entries = entries; StartTick = startTick; Active = true; _deathEvents.Clear();
        foreach (var e in entries) { e.State = DefenseBotState.Waiting; e.StartedTick = null; e.HandoffTick = null; }
    }
    public void Tick(int now)
    {
        if (!Active) return;
        foreach (var e in Entries)
        {
            if (e.State == DefenseBotState.Dead) continue;
            if (_deathEvents.Contains(e.Slot)) { engine.Stop(e.Slot); e.State = DefenseBotState.Dead; continue; }
            if (!engine.IsConnected(e.Slot)) throw new InvalidOperationException($"Defense 队员 {e.Role} 意外断线。");
            if (!engine.IsAlive(e.Slot)) { engine.Stop(e.Slot); e.State = DefenseBotState.Dead; continue; }
            if (e.State == DefenseBotState.Waiting && now >= checked(StartTick + e.OffsetTicks))
            {
                if (!engine.Start(e.Slot)) throw new InvalidOperationException($"Defense 队员 {e.Role} 无法开始回放。");
                e.StartedTick = now; e.State = DefenseBotState.Replaying;
            }
            if (e.State != DefenseBotState.Replaying || e.StartedTick is not int started) continue;
            bool playing = engine.IsPlaying(e.Slot);
            int elapsed = now - started;
            if (!playing && elapsed < Math.Min(e.HandoffTicks, e.TotalTicks) - 2)
                throw new InvalidOperationException($"Defense 队员 {e.Role} 轨迹意外提前停止。");
            if (e.Automatic && elapsed > e.TotalTicks + 128) throw new TimeoutException($"Defense 队员 {e.Role} 原生回放结束超时。");
            if (elapsed >= e.HandoffTicks && (!e.Automatic || !playing))
            {
                if (!engine.HandoffToCombatAI(e.Slot)) throw new InvalidOperationException($"Defense 队员 {e.Role} AI 交接失败。");
                e.State = DefenseBotState.CombatAI; e.HandoffTick = now;
            }
        }
    }
    public void Cancel()
    {
        Active = false;
        foreach (var e in Entries) try { engine.Stop(e.Slot); } catch { /* release every slot */ }
    }
    public static int HandoffTicks(int? milliseconds, Track track, double tickInterval)
    {
        if (milliseconds == null) return track.Ticks.Length;
        if (milliseconds < 0 || milliseconds > 180000 || !double.IsFinite(tickInterval) || tickInterval <= 0 ||
            milliseconds / 1000d > track.Ticks.Length * tickInterval)
            throw new ArgumentException("AI 交接时间不能超出该队员轨迹长度。");
        return checked((int)Math.Ceiling(milliseconds.Value / (tickInterval * 1000)));
    }
}

public enum DefenseResult { Success, Failure, Timeout, Draw }
public sealed class DefenseStatistics
{
    public int Rounds { get; private set; }
    public int Success { get; private set; }
    public int Failure { get; private set; }
    public int Timeouts { get; private set; }
    public int Draws { get; private set; }
    public DefenseResult? LastResult { get; private set; }
    public void Finish(DefenseResult result)
    {
        Rounds++; LastResult = result;
        if (result == DefenseResult.Success) Success++; else if (result == DefenseResult.Failure) Failure++;
        else if (result == DefenseResult.Draw) Draws++; else Timeouts++;
    }
    public static DefenseResult? Evaluate(bool defenderAlive, int dead, int total, bool timedOut)
    {
        if (!defenderAlive) return DefenseResult.Failure;
        if (total > 0 && dead == total) return DefenseResult.Success;
        return timedOut ? DefenseResult.Timeout : null;
    }
    public static DefenseResult? EvaluateTeam(int aliveCT, int effectiveCT, int deadT, int totalT, bool timedOut)
    {
        if (effectiveCT < 1) return null; // Nobody remains: abort rather than scoring a disconnect as a death.
        if (aliveCT == 0 && totalT > 0 && deadT == totalT) return DefenseResult.Draw;
        if (aliveCT == 0) return DefenseResult.Failure;
        if (totalT > 0 && deadT == totalT) return DefenseResult.Success;
        return timedOut ? DefenseResult.Timeout : null;
    }
}

public sealed class DefenseSettings
{
    public Guid TacticId { get; set; }
    public string Map { get; set; } = "";
    public Track? Defender { get; set; }
    public Dictionary<string, Track> Defenders { get; set; } = new(StringComparer.Ordinal);
    public void MigrateOwner(string owner)
    {
        if (Defender != null && !Defenders.ContainsKey(owner)) Defenders[owner] = Defender;
    }
    public int Difficulty { get; set; } = 2;
    public bool Repeat { get; set; } = true;
    public int RoundSeconds { get; set; } = 120;
    public Dictionary<int, int?> HandoffMilliseconds { get; set; } = [];
    public void Validate(Tactic tactic)
    {
        if (tactic.Team != 2 || tactic.Id != TacticId || tactic.Map != Map || Difficulty is < 1 or > 4 || RoundSeconds is < 10 or > 600 || HandoffMilliseconds.Count > 5)
            throw new InvalidDataException("Defense 场景或 T 方战术无效。");
        foreach (var (role, ms) in HandoffMilliseconds)
        {
            if (!tactic.Tracks.TryGetValue(role, out var track)) throw new InvalidDataException("Defense 场景含不存在的队员。");
            DefenseBotController.HandoffTicks(ms, track, tactic.TickInterval);
        }
        if (Defenders.Count > 32 || Defenders.Keys.Any(k => !AccessPolicy.IsSteamId64(k)))
            throw new InvalidDataException("Defense 防守者配置数量或 SteamID64 无效。");
        foreach (var defender in Defenders.Values.Concat(Defender == null ? [] : new[] { Defender }))
        {
            if (defender == null) throw new InvalidDataException("Defense 防守者配置为空。");
            var p = defender.Initial;
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z) || !float.IsFinite(p.Pitch) || !float.IsFinite(p.Yaw) ||
                !float.IsFinite(p.Roll) || defender.Equipment.Length > 24 || defender.Ammo.Length > 256 ||
                defender.Equipment.Any(e => !System.Text.RegularExpressions.Regex.IsMatch(e.ClassName, "^weapon_[a-z0-9_]{1,48}$")))
                throw new InvalidDataException("Defense 防守位置或装备无效。");
        }
    }
}
