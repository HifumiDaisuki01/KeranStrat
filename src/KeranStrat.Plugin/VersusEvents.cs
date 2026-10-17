using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using KeranStrat.Core;

namespace KeranStrat.Plugin;

public sealed partial class KeranStratPlugin
{
    private bool VersusRespawnAllowed => _modeVersus && _trainingMode == "Practice" && _practice.Active && !_modeChanging &&
        !DefenseLease && !DefenseBusy && _versusState == "idle";
    private void VersusTeamChanged(CCSPlayerController? p, int team, int oldTeam)
    {
        if (p == null || !p.IsValid) return;
        _versusRespawn.Cancel(p.Slot);
        if (!VersusRespawnAllowed || team == oldTeam || team is not (2 or 3) || !VersusAuthorized(p)) return;
        _versusRespawn.Queue(PracticeIdentityOf(p), team, Server.TickCount, DefenseTicks(250), DefenseTicks(5000));
    }
    private void VersusRespawnTick()
    {
        if (!VersusRespawnAllowed) { _versusRespawn.Clear(); return; }
        foreach (int slot in _versusRespawn.Slots)
        {
            var p = NativeReplayEngine.Player(slot);
            if (p == null || !VersusAuthorized(p)) { _versusRespawn.Cancel(slot); continue; }
            if (_versusRespawn.Take(PracticeIdentityOf(p), p.TeamNum, p.PawnIsAlive, Server.TickCount))
                try { p.Respawn(); } catch (Exception e) { Log("KSV 切队复活失败：" + e.Message); }
        }
    }
    private void VersusDeath(CCSPlayerController? p)
    {
        if (p == null) return;
        var owned = _versusBots.Values.FirstOrDefault(b => b.Serial == p.EntityHandle.Raw && b.UserId == p.UserId);
        if (owned != null) { _versusCoordinator?.MarkDead(p.Slot); XRayRemove(owned.Key); }
    }
}
