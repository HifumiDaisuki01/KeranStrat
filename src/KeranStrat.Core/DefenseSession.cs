namespace KeranStrat.Core;

public sealed record DefenseIdentity(string SteamId, int Slot, int UserId, uint Handle);

public sealed class DefenseParticipant(DefenseIdentity identity, string name)
{
    public DefenseIdentity Identity { get; } = identity;
    public string Name { get; } = name;
    public bool Dead { get; set; }
    public bool PendingLeave { get; set; }
}

// The live roster is distinct from saved positions. A controller serial prevents slot reuse.
public sealed class DefenseSession(DefenseParticipant owner)
{
    public string OwnerId { get; } = owner.Identity.SteamId;
    private readonly Dictionary<string, DefenseParticipant> _members = new(StringComparer.Ordinal) { [owner.Identity.SteamId] = owner };
    private readonly Dictionary<string, DefenseParticipant> _round = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, DefenseParticipant> Members => _members;
    public IReadOnlyDictionary<string, DefenseParticipant> Round => _round;
    public bool IsOwner(DefenseIdentity identity) => identity.SteamId == OwnerId && Contains(identity);
    public bool Contains(DefenseIdentity identity) => _members.TryGetValue(identity.SteamId, out var member) && member.Identity == identity;
    public bool Join(DefenseParticipant participant)
    {
        if (_members.TryGetValue(participant.Identity.SteamId, out var existing))
        {
            if (existing.Identity != participant.Identity) throw new InvalidOperationException("成员身份已变化，请重新加入会话。");
            return false;
        }
        if (_members.Count >= 5) throw new InvalidOperationException("防守队伍已满（5/5）。");
        _members.Add(participant.Identity.SteamId, participant); return true;
    }
    public bool Remove(DefenseIdentity identity)
    {
        if (!Contains(identity)) return false;
        _members.Remove(identity.SteamId); _round.Remove(identity.SteamId); return true;
    }
    public void BeginRound()
    {
        _round.Clear();
        foreach (var (id, member) in _members) { member.Dead = false; _round.Add(id, member); }
    }
    public void ApplyPendingLeaves()
    {
        foreach (var member in _members.Values.Where(m => m.PendingLeave).ToArray()) Remove(member.Identity);
    }
}
