namespace KeranStrat.Core;

public readonly record struct PracticeIdentity(ulong SteamId, int Slot, int UserId, uint Handle);

// One request per slot: a newer team change replaces the old request.
public sealed class PracticeTeamRespawn
{
    public static bool Allows(bool practice, bool active, bool changing, bool defenseMode, bool defenseLease, bool defenseBusy) =>
        practice && active && !changing && !defenseMode && !defenseLease && !defenseBusy;
    private readonly Dictionary<int, (PracticeIdentity Identity, int Team, int Due, int Expires)> _pending = [];
    public int[] Slots => _pending.Keys.ToArray();
    public void Clear() => _pending.Clear();
    public void Cancel(int slot) => _pending.Remove(slot);
    public void Queue(PracticeIdentity identity, int team, int now, int delay, int lifetime)
    {
        if (team is not (2 or 3)) { Cancel(identity.Slot); return; }
        _pending[identity.Slot] = (identity, team, now + delay, now + lifetime);
    }
    public bool Take(PracticeIdentity identity, int team, bool alive, int now)
    {
        if (!_pending.TryGetValue(identity.Slot, out var request)) return false;
        if (request.Identity != identity || now > request.Expires)
        { Cancel(identity.Slot); return false; }
        if (now < request.Due) return false;
        Cancel(identity.Slot); // Remove before calling the engine; no retry/death loop.
        return team == request.Team && !alive;
    }
}
