using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using KeranStrat.Core;

namespace KeranStrat.Plugin;

public sealed partial class KeranStratPlugin
{
    private readonly PracticeTeamRespawn _practiceTeamRespawn = new();
    private bool KsPracticeRespawnEnabled => PracticeTeamRespawn.Allows(_trainingMode == "Practice", _practice.Active,
        _modeChanging, _modeDefense || _modeVersus, DefenseLease || VersusLease, DefenseBusy);
    private static PracticeIdentity PracticeIdentityOf(CCSPlayerController p) =>
        new(p.AuthorizedSteamID!.SteamId64, p.Slot, p.UserId!.Value, p.EntityHandle.Raw);
    private bool KsPracticeHuman(CCSPlayerController p) => p.IsValid && !p.IsBot && !p.IsHLTV && p.UserId != null &&
        AccessPolicy.Allows(Config.Enabled, _allowed, p.AuthorizedSteamID?.SteamId64.ToString());
    private void PracticeTeamChanged(CCSPlayerController? p, int team, int oldTeam)
    {
        if (p == null || !p.IsValid) return;
        _practiceTeamRespawn.Cancel(p.Slot);
        if (!KsPracticeRespawnEnabled || team == oldTeam || team is not (2 or 3) || !KsPracticeHuman(p)) return;
        _practiceTeamRespawn.Queue(PracticeIdentityOf(p), team, Server.TickCount,
            ReplayCoordinator.ToTicks(250, Server.TickInterval), ReplayCoordinator.ToTicks(5000, Server.TickInterval));
    }
    private void PracticeRespawnTick()
    {
        if (!KsPracticeRespawnEnabled) { _practiceTeamRespawn.Clear(); return; }
        foreach (int slot in _practiceTeamRespawn.Slots)
        {
            var p = NativeReplayEngine.Player(slot);
            if (p == null || !KsPracticeHuman(p)) { _practiceTeamRespawn.Cancel(slot); continue; }
            if (_practiceTeamRespawn.Take(PracticeIdentityOf(p), p.TeamNum, p.PawnIsAlive, Server.TickCount))
                try { p.Respawn(); }
                catch (Exception e) { Log("KS 切队复活失败：" + e.Message); }
        }
    }
}
