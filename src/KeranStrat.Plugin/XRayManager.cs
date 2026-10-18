using System.Drawing;
using System.Text.Json;
using System.Text.Json.Nodes;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using KeranStrat.Core;

namespace KeranStrat.Plugin;

public sealed partial class KeranStratPlugin
{
    private sealed record XRayProxy(string Key, uint PawnSerial, CDynamicProp Relay, CDynamicProp Glow, int ReadyTick)
    {
        public bool Retiring { get; set; }
        public bool Initialized { get; set; }
    }
    private readonly Dictionary<string, XRayProxy> _xray = [];
    private readonly Dictionary<string, bool> _ksXrayPreferences = [], _versusXrayPreferences = [];
    private readonly object _configurationWriteGate = new();
    private bool _xrayVersus, _xrayFailed;
    private bool XRayDefenseBlocked => _modeDefense || DefenseLease || DefenseBusy;
    private bool XRayViewer(CCSPlayerController? p, bool versus)
    {
        if (_versusTestPhase != 0) return false;
        if (p is not { IsValid: true, IsBot: false, IsHLTV: false } || p.TeamNum != 1 || p.AuthorizedSteamID == null) return false;
        string id = p.AuthorizedSteamID.SteamId64.ToString();
        bool authorized = versus ? AccessPolicy.Allows(Config.VersusEnabled, _versusAllowed, id) : AccessPolicy.Allows(Config.Enabled, _allowed, id);
        bool enabled = (versus ? _versusXrayPreferences : _ksXrayPreferences).GetValueOrDefault(id, versus ? Config.VersusXRayDefault : Config.KsXRayDefault);
        return XRayPolicy.CanSee(_trainingMode == "Practice" && !_modeChanging, versus ? _versusState == "playing" : _state == "playing" && !VersusLease,
            XRayDefenseBlocked, authorized, true, enabled);
    }
    private void XRayTransmit(CCheckTransmitInfoList list)
    {
        foreach (var (info, viewer) in list)
        {
            bool allowed = false;
            try { allowed = !_xrayFailed && XRayViewer(viewer, _xrayVersus); } catch { /* Unauthenticated or invalid viewer: deny. */ }
            foreach (var proxy in _xray.Values)
            {
                var pawn = new CHandle<CCSPlayerPawn>(proxy.PawnSerial).Value;
                bool visible = allowed && proxy.Initialized && !proxy.Retiring && Server.TickCount >= proxy.ReadyTick &&
                    pawn is { IsValid: true, LifeState: 0 };
                foreach (var entity in new[] { proxy.Relay, proxy.Glow })
                {
                    if (!entity.IsValid) continue;
                    if (visible) info.TransmitEntities.Add(entity);
                    else { info.TransmitEntities.Remove(entity); if (!info.TransmitAlways.Equals(default(CFixedBitVecBase))) info.TransmitAlways.Remove(entity); }
                }
                // The engine owns pawn transmission, including death and ragdoll transitions.
                // Forcing a pawn back into the snapshot can reference a removed ServerClass.
            }
        }
    }
    private void XRayTick()
    {
        try
        {
            // The console diagnostic owns its temporary pairs and checks them after staging.
            if (_versusTestPhase != 0) return;
            bool versus = _versusState == "playing";
            if (_xrayFailed || XRayDefenseBlocked || _modeChanging || _trainingMode != "Practice" || (!versus && (_state != "playing" || VersusLease)) ||
                !Utilities.GetPlayers().Any(p => XRayViewer(p, versus))) { XRayClear(); return; }
            if (_xray.Count != 0 && _xrayVersus != versus) XRayClear();
            _xrayVersus = versus;
            var targets = new Dictionary<string, CCSPlayerPawn>();
            if (versus)
            {
                foreach (var b in _versusBots.Values) if (VersusResolve(b) is { PawnIsAlive: true, PlayerPawn.Value: { IsValid: true } pawn }) targets[b.Key] = pawn;
            }
            else foreach (var b in _bots)
                if (NativeReplayEngine.Player(b.Slot) is { IsValid: true, IsBot: true, PawnIsAlive: true } bot && _botIdentities.GetValueOrDefault(b.Slot) == bot.Handle && bot.PlayerPawn.Value is { IsValid: true } pawn)
                    targets[b.Role.ToString()] = pawn;
            foreach (var (key, proxy) in _xray.ToArray())
                if (!targets.TryGetValue(key, out var pawn) || pawn.EntityHandle.Raw != proxy.PawnSerial || !proxy.Relay.IsValid || !proxy.Glow.IsValid) XRayRemove(key);
            foreach (var (key, pawn) in targets)
            {
                if (!_xray.TryGetValue(key, out var proxy)) { proxy = XRayCreate(key, pawn); _xray[key] = proxy; }
                if (proxy.Retiring || !proxy.Initialized) continue;
                string color = (versus ? Config.VersusXRayColors : Config.KsXRayColors)[key];
                var desired = ColorTranslator.FromHtml(color);
                if (proxy.Glow.Glow.GlowColorOverride != desired)
                { proxy.Glow.Glow.GlowColorOverride = desired; Utilities.SetStateChanged(proxy.Glow, "CBaseModelEntity", "m_Glow"); }
            }
        }
        catch (Exception e) { _xrayFailed = true; XRayClear(); Log("X 光已安全禁用：" + e.Message); }
    }
    private XRayProxy XRayCreate(string key, CCSPlayerPawn pawn)
    {
        CDynamicProp? relay = null, glow = null;
        bool relaySpawned = false, glowSpawned = false;
        try
        {
            string model = pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName ?? throw new InvalidOperationException("缺少模型。");
            relay = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic") ?? throw new InvalidOperationException("无法创建轮廓跟随实体。");
            glow = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic") ?? throw new InvalidOperationException("无法创建轮廓实体。");
            relay.SetModel(model); relay.Spawnflags = 256; relay.DispatchSpawn(); relaySpawned = true;
            glow.SetModel(model); glow.Spawnflags = 256; glow.DispatchSpawn(); glowSpawned = true;
            var proxy = new XRayProxy(key, pawn.EntityHandle.Raw, relay, glow, Server.TickCount + 2);
            Server.NextFrame(() =>
            {
                if (!_xray.TryGetValue(key, out var current) || !ReferenceEquals(current, proxy) || proxy.Retiring) return;
                try
                {
                    if (!relay.IsValid || !glow.IsValid || !pawn.IsValid || pawn.EntityHandle.Raw != proxy.PawnSerial || pawn.LifeState != 0)
                    { XRayRemove(key); return; }
                    // Replicated properties and FollowEntity are unsafe on staging entities.
                    relay.RenderMode = RenderMode_t.kRenderNone;
                    glow.Glow.GlowColorOverride = ColorTranslator.FromHtml((_xrayVersus ? Config.VersusXRayColors : Config.KsXRayColors)[key]);
                    glow.Glow.GlowRange = 10000; glow.Glow.GlowRangeMin = 0; glow.Glow.GlowTeam = -1; glow.Glow.GlowType = 3;
                    relay.AcceptInput("FollowEntity", pawn, relay, "!activator"); glow.AcceptInput("FollowEntity", relay, glow, "!activator");
                    Utilities.SetStateChanged(glow, "CBaseModelEntity", "m_Glow");
                    proxy.Initialized = true;
                }
                catch (Exception e) { _xrayFailed = true; XRayClear(); Log("X 光初始化已安全停止：" + e.Message); }
            });
            return proxy;
        }
        catch
        {
            // A partially created prop must leave staging before requesting removal.
            foreach (var (entity, spawned) in new[] { (glow, glowSpawned), (relay, relaySpawned) })
                if (entity is { IsValid: true })
                    try { if (!spawned) entity.DispatchSpawn(); entity.Remove(); }
                    catch (Exception e) { Log("未完成轮廓实体清理失败：" + e.Message); }
            throw;
        }
    }
    private void XRayRemove(string key)
    {
        if (!_xray.TryGetValue(key, out var proxy)) return;
        // Remove() queues engine deletion: IsValid may remain true until the next frame.
        // Deny transmission BEFORE requesting deletion, never re-add a retiring entity.
        proxy.Retiring = true;
        foreach (var entity in new[] { proxy.Glow, proxy.Relay })
            try { if (entity.IsValid) entity.Remove(); }
            catch (Exception e) { if (!_xrayFailed) Log("轮廓清理待重试：" + e.Message); _xrayFailed = true; }
        // Retain failed removals in the transmit deny list until the engine removes them.
        if (!proxy.Glow.IsValid && !proxy.Relay.IsValid) _xray.Remove(key);
    }
    private void XRayClear() { foreach (string key in _xray.Keys.ToArray()) XRayRemove(key); }
    private void PersistXRayPalette(bool versus, Dictionary<string, string> colors)
    {
        lock (_configurationWriteGate)
        {
            PalettePersistence.Save(ConfigurationPath, versus, colors);
            if (versus) Config.VersusXRayColors = colors; else Config.KsXRayColors = colors;
            // FileSystemWatcher performs one normal reload once playback becomes idle.
        }
    }
    private void XRayCommand(CCSPlayerController? p, CommandInfo command, bool versus)
    {
        if (p == null) throw new InvalidOperationException("X 光偏好和颜色命令需要对应白名单真人。");
        string? id = p.AuthorizedSteamID?.SteamId64.ToString();
        if (!AccessPolicy.Allows(versus ? Config.VersusEnabled : Config.Enabled, versus ? _versusAllowed : _allowed, id)) throw new InvalidOperationException("没有此模式的权限。");
        if (XRayDefenseBlocked) throw new InvalidOperationException("Defense 环境永久禁止 X 光。");
        string action = command.GetArg(2).ToLowerInvariant();
        var preferences = versus ? _versusXrayPreferences : _ksXrayPreferences;
        var colors = new Dictionary<string, string>(versus ? Config.VersusXRayColors : Config.KsXRayColors);
        if (action is "on" or "off")
        {
            preferences[id!] = action == "on"; XRayTick();
            command.ReplyToCommand("X 光偏好已" + (action == "on" ? "开启；只在获授权的观察者状态与对应回放中显示。" : "关闭。")); return;
        }
        if (action == "colors") { command.ReplyToCommand(string.Join(" / ", colors.OrderBy(kv => kv.Key).Select(kv => kv.Key + " " + kv.Value))); return; }
        if (action == "resetcolors") colors = versus ? XRayPolicy.VersusDefaults() : XRayPolicy.KsDefaults();
        else if (action == "color")
        {
            string key = versus ? VersusScene.Key(VersusTeam(command.GetArg(3)), VersusRole(command.GetArg(4))) : VersusRole(command.GetArg(3)).ToString();
            colors[key] = XRayPolicy.Normalize(command.GetArg(versus ? 5 : 4));
        }
        else throw new ArgumentException("xray on/off/colors/resetcolors，或 xray color " + (versus ? "t/ct " : "") + "1-5 RGB。");
        PersistXRayPalette(versus, colors); XRayTick(); command.ReplyToCommand("配色已更新并原子保存，其他配置保持原值。");
    }
}
