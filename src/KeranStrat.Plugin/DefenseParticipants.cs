using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using KeranStrat.Core;

namespace KeranStrat.Plugin;

public sealed partial class KeranStratPlugin
{
    private DefenseSession? _defenseSession;
    private static void DefenseBotFunds(CCSPlayerController p)
    {
        // Practice allows buying everywhere; combat bots must keep the recorded loadout.
        if (p.IsBot && p.InGameMoneyServices is { } money && money.Account != 0) money.Account = 0;
    }
    private bool DefenseLease => _defenseSession != null || _defenseLoading;
    private bool DefenseDiagnostic => (_defenseTestPhase != 0 || _coopTestPhase != 0) && !OtherHumans(0);
    private DefenseIdentity DefenseIdentityOf(CCSPlayerController player)
    {
        string? id = player.IsBot && DefenseDiagnostic ? (76561198090000000UL + (uint)(player.UserId ?? 0)).ToString()
            : player.AuthorizedSteamID?.SteamId64.ToString();
        if (!player.IsValid || player.UserId is not int userId || !AccessPolicy.IsSteamId64(id ?? "") ||
            (player.IsBot && !DefenseDiagnostic)) throw new InvalidOperationException("需要已完成 Steam 认证的真人玩家。");
        return new DefenseIdentity(id!, player.Slot, userId, player.EntityHandle.Raw);
    }
    private CCSPlayerController? DefenseResolve(DefenseParticipant member)
    {
        var p = NativeReplayEngine.Player(member.Identity.Slot);
        if (p is not { IsValid: true } || p.UserId != member.Identity.UserId || p.EntityHandle.Raw != member.Identity.Handle) return null;
        if (p.IsBot && (_defenseTestPhase != 0 || _coopTestPhase != 0))
            return member.Identity.SteamId == (76561198090000000UL + (uint)member.Identity.UserId).ToString() ? p : null;
        try { return DefenseIdentityOf(p) == member.Identity ? p : null; } catch (InvalidOperationException) { return null; }
    }
    private DefenseParticipant DefenseCreateParticipant(CCSPlayerController p) => new(DefenseIdentityOf(p), p.PlayerName);
    private void DefenseRequireMember(CCSPlayerController p)
    {
        if (_defenseSession == null || !_defenseSession.Contains(DefenseIdentityOf(p)))
            throw new InvalidOperationException("请先 !ksd join 加入当前防守队伍。");
    }
    private bool DefenseAuthorized(CCSPlayerController p) => p.IsBot && DefenseDiagnostic ||
        AccessPolicy.Allows(Config.DefenseEnabled, _defenseAllowed, p.AuthorizedSteamID?.SteamId64.ToString());
    private void CloseDefense(string reason)
    {
        StopDefense(reason);
        if (_defenseSession != null) DefenseNotify("防守会话已解散：" + reason);
        _defenseSession = null; _defenseTactic = null; _defenseSettings = null;
        _defenseOwner = 0; _defenseOwnerSlot = -1;
    }
    private void DefenseDisconnected(CCSPlayerController p)
    {
        // Disconnect events may resolve a slot already reused by a different controller.
        // Validate saved Steam/UserId/entity identities after engine removal instead.
        Server.NextFrame(DefenseValidateMembers);
    }
    private void DefenseValidateMembers()
    {
        if (_defenseSession == null) return;
        foreach (var member in _defenseSession.Members.Values.ToArray())
        {
            var p = DefenseResolve(member);
            if (p != null && p.TeamNum == 3 && Config.DefenseEnabled && DefenseAuthorized(p)) continue;
            if (member.Identity.SteamId == _defenseSession.OwnerId) { CloseDefense("房主离线、离开 CT 或 Defense 权限已移除"); return; }
            _defenseSession.Remove(member.Identity);
            // Only a previously enrolled, identity-verified player is moved to spectator.
            if (p is { IsValid: true } && DefenseBusy && p.TeamNum is 2 or 3) p.ChangeTeam(CounterStrikeSharp.API.Modules.Utils.CsTeam.Spectator);
            DefenseNotify(member.Name + " 已离开 CT 或失去权限，退出本次防守。");
        }
    }
    private CCSPlayerController[] DefenseOutsiders() => Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot && !p.IsHLTV && p.TeamNum is 2 or 3 &&
        (_defenseSession == null || !_defenseSession.Members.Values.Any(m => m.Identity.UserId == p.UserId && m.Identity.Handle == p.EntityHandle.Raw))).ToArray();
    private void DefenseMarkDeath(CCSPlayerController? p)
    {
        if (p == null || _defenseSession == null || _defenseState != "running") return;
        if (_defenseBots.Any(b => b.Handle == p.Handle && b.UserId == p.UserId)) _defenseController?.MarkDead(p.Slot);
        var member = _defenseSession.Round.Values.FirstOrDefault(m => m.Identity.UserId == p.UserId && m.Identity.Handle == p.EntityHandle.Raw);
        if (member != null) member.Dead = true;
    }
    private void DefenseHandleSpawn(CCSPlayerController? p)
    {
        if (p == null || _defenseSession == null || _defenseState is not ("running" or "preparing" or "between")) return;
        var member = _defenseSession.Round.Values.FirstOrDefault(m => m.Identity.UserId == p.UserId && m.Identity.Handle == p.EntityHandle.Raw);
        if (member == null) return;
        // Sticky per-round death prevents a plugin or engine respawn from re-entering combat.
        if (_defenseState == "running" && member.Dead)
            Server.NextFrame(() => { var current = DefenseResolve(member); if (_defenseState == "running" && member.Dead && current is { PawnIsAlive: true }) current.CommitSuicide(false, true); });
    }
    private void DefenseTeam(Action<string> reply)
    {
        if (_defenseSession == null) { reply("当前没有 Defense 会话，先由房主加载战术。"); return; }
        reply($"当前防守队伍：{_defenseSession.Members.Count}/5；战术：{_defenseTactic?.Name}");
        int ready = 0;
        foreach (var member in _defenseSession.Members.Values)
        {
            bool has = _defenseSettings?.Defenders.ContainsKey(member.Identity.SteamId) == true;
            if (has) ready++;
            reply($"{member.Name} [{(member.Identity.SteamId == _defenseSession.OwnerId ? "房主" : "成员")}] - {(has ? "已保存位置" : "未准备")} {(member.PendingLeave ? "（本轮结束退出）" : "")}");
        }
        reply($"准备状态：{ready}/{_defenseSession.Members.Count}");
    }
}
