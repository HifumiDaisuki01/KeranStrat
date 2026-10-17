using System.Text.Json;
using BotControllerApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using KeranStrat.Core;

namespace KeranStrat.Plugin;

public sealed partial class KeranStratPlugin
{
    private sealed record VersusBot(string Key, int Slot, int UserId, uint Serial);
    private readonly Dictionary<string, VersusBot> _versusBots = [];
    private readonly Dictionary<uint, VersusBot> _versusRetiring = [];
    private readonly HashSet<uint> _versusSpawnBaseline = [], _versusEntityBaseline = [];
    private readonly Dictionary<uint, string> _versusTemporary = [];
    private Dictionary<string, string>? _versusCvars;
    private VersusNativeEngine? _versusEngine;
    private VersusCoordinator? _versusCoordinator;
    private int _versusDeadline, _versusReadyAt, _versusRoundStart, _versusSettleAt, _versusRounds;
    private string _versusLastResult = "none";
    private static readonly string[] VersusScopedCvars = ["bot_dont_shoot", "bot_difficulty", "bot_stop", "bot_zombie", "bot_quota", "bot_quota_mode", "mp_respawn_immunitytime", "mp_respawn_on_death_ct", "mp_respawn_on_death_t"];
    private CCSPlayerController? VersusResolve(VersusBot bot)
    {
        var p = NativeReplayEngine.Player(bot.Slot);
        return p is { IsValid: true, IsBot: true } && p.UserId == bot.UserId && p.EntityHandle.Raw == bot.Serial ? p : null;
    }
    private bool VersusHasOutsiders(CCSPlayerController? except = null) => Utilities.GetPlayers().Any(p => p.IsValid && !p.IsBot && !p.IsHLTV && p.TeamNum is 2 or 3 && (except == null || p.EntityHandle.Raw != except.EntityHandle.Raw));
    private void StartVersus(CCSPlayerController? owner)
    {
        if (owner == null && _versusTestPhase == 0) throw new InvalidOperationException("缺少 KSV 操作者。");
        _versusScene!.Validate(MaxTicks, playable: true);
        if (_versusScene.Map != Server.MapName || _versusScene.GameVersion != _gameVersion || Math.Abs(_versusScene.TickInterval - Server.TickInterval) > .000001)
            throw new InvalidOperationException("场景地图／游戏版本／Tick 间隔不兼容。");
        if (_versusScene.Tracks.Keys.GroupBy(k => VersusScene.Parse(k).Team).Any(g => g.Count() > Config.VersusMaxPlayersPerTeam)) throw new InvalidOperationException("角色超出配置人数上限。");
        if (VersusHasOutsiders(owner)) throw new InvalidOperationException("请其他真人先转为观察者；KSV 第一版仅允许 BOT 参战。");
        if (Utilities.GetPlayers().Any(p => p.IsValid && p.IsBot && !_versusRetiring.ContainsKey(p.EntityHandle.Raw)))
            throw new InvalidOperationException("服务器存在其他 BOT，请先由所属系统清理。");
        _versusCvars = VersusScopedCvars.ToDictionary(k => k, k => ConVar.Find(k)?.StringValue ?? throw new InvalidOperationException("缺少参数 " + k));
        string recovery = Path.Combine(_dataRoot, "versus-recovery.json");
        File.WriteAllText(recovery + ".tmp", JsonSerializer.Serialize(_versusCvars)); File.Move(recovery + ".tmp", recovery, true);
        _versusEngine = new(_api!); _versusCoordinator = new(_versusEngine); _versusError = "none";
        if (owner?.TeamNum is 2 or 3) owner.ChangeTeam(CsTeam.Spectator);
        foreach (string cmd in new[] { "bot_dont_shoot 0", "bot_stop 0", "bot_zombie 0", "bot_quota_mode normal", "mp_respawn_immunitytime 0", "mp_respawn_on_death_ct 0", "mp_respawn_on_death_t 0" }) Server.ExecuteCommand(cmd);
        try { BeginVersusRound(); } catch { StopVersus("启动失败"); throw; }
    }
    private void BeginVersusRound()
    {
        CleanupVersusRound(); _versusState = "cleaning"; _versusReadyAt = 0;
        _versusDeadline = Server.TickCount + DefenseTicks(10000); _versusSettleAt = 0;
    }
    private void SpawnVersusRound()
    {
        // Prevent unbound bots from fighting before both teams have been restored and locked.
        Server.ExecuteCommand("bot_dont_shoot 1");
        _versusSpawnBaseline.Clear(); foreach (var p in Utilities.GetPlayers()) if (p.IsValid) _versusSpawnBaseline.Add(p.EntityHandle.Raw);
        _versusEntityBaseline.Clear();
        foreach (var name in VersusEntityClasses()) foreach (var e in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(name)) _versusEntityBaseline.Add(e.EntityHandle.Raw);
        foreach (var key in _versusScene!.Tracks.Keys.Order()) Server.ExecuteCommand("bot_add_" + (VersusScene.Parse(key).Team == 2 ? "t" : "ct"));
        Server.ExecuteCommand("bot_quota " + _versusScene.Tracks.Count);
        _versusDeadline = Server.TickCount + DefenseTicks(15000); _versusReadyAt = 0; _versusState = "preparing";
    }
    private void PrepareVersusTick()
    {
        if (Server.TickCount > _versusDeadline) throw new TimeoutException("双方 BOT 出生／准备超时。");
        if (_versusReadyAt != 0)
        {
            if (Server.TickCount < _versusReadyAt) return;
            int start = Server.TickCount + 1;
            Server.ExecuteCommand("bot_dont_shoot 0");
            _versusCoordinator!.Begin(start, _versusBots.Select(kv =>
            {
                var track = _versusScene!.Tracks[kv.Key]; var handoff = _versusScene.Handoff.GetValueOrDefault(kv.Key);
                return new VersusEntry(kv.Key, kv.Value.Slot, DefenseTicks(track.OffsetMs), track.Ticks.Length,
                    DefenseBotController.HandoffTicks(handoff, track, _versusScene.TickInterval), handoff == null);
            }).ToArray(), _versusScene!.Mode == "hybrid", Server.TickInterval);
            _versusRoundStart = start; _versusState = "playing"; VersusNotify("双阵营同步开始。"); return;
        }
        foreach (var (key, track) in _versusScene!.Tracks.OrderBy(kv => kv.Key))
        {
            if (_versusBots.ContainsKey(key)) continue;
            int team = VersusScene.Parse(key).Team;
            var bot = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && p.IsBot && p.UserId != null && p.TeamNum == team && !_versusSpawnBaseline.Contains(p.EntityHandle.Raw));
            if (bot == null) continue;
            if (!bot.PawnIsAlive) { bot.Respawn(); continue; }
            var identity = new VersusBot(key, bot.Slot, bot.UserId!.Value, bot.EntityHandle.Raw);
            _versusBots[key] = identity; _versusSpawnBaseline.Add(identity.Serial);
            DefenseBotFunds(bot); _versusEngine!.Native.Prepare(bot, track);
            if (bot.PlayerPawn.Value is { } pawn)
            {
                pawn.GunGameImmunity = false;
                if (pawn.WeaponServices != null) foreach (var handle in pawn.WeaponServices.MyWeapons)
                    if (handle.Value is { IsValid: true } item) _versusTemporary[item.EntityHandle.Raw] = item.DesignerName;
            }
        }
        if (_versusBots.Count == _versusScene.Tracks.Count) { _versusReadyAt = Server.TickCount + DefenseTicks(3000); VersusNotify("双方就绪，3 秒后同步回放。"); }
    }
    private IEnumerable<string> VersusEntityClasses() => DefenseTemporaryClasses.Concat(_versusScene?.Tracks.Values.SelectMany(t => t.Equipment).Select(e => e.ClassName) ?? []).Distinct(StringComparer.Ordinal);
    private void TrackVersusEntities()
    {
        foreach (var name in VersusEntityClasses()) foreach (var e in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(name))
        {
            if (_versusEntityBaseline.Contains(e.EntityHandle.Raw) || _versusTemporary.ContainsKey(e.EntityHandle.Raw)) continue;
            var pawn = e.OwnerEntity.Value;
            if (pawn == null && name.EndsWith("_projectile", StringComparison.Ordinal)) pawn = e.As<CBaseGrenade>().Thrower.Value;
            if (pawn is { IsValid: true } projectile && projectile.DesignerName.EndsWith("_projectile", StringComparison.Ordinal)) pawn = projectile.As<CBaseGrenade>().Thrower.Value;
            var controller = pawn is { IsValid: true, DesignerName: "player" } ? pawn.As<CCSPlayerPawn>().Controller.Value : null;
            if (controller != null && _versusBots.Values.Any(b => b.Serial == controller.EntityHandle.Raw)) _versusTemporary[e.EntityHandle.Raw] = name;
        }
    }
    private void CleanupVersusRound()
    {
        XRayClear();
        _versusCoordinator?.Cancel();
        // Include bots created by this prepare operation but not bound yet.
        if (_versusState == "preparing" && _versusScene != null)
            foreach (var p in Utilities.GetPlayers().Where(p => p.IsValid && p.IsBot && p.UserId != null && !_versusSpawnBaseline.Contains(p.EntityHandle.Raw)).Take(_versusScene.Tracks.Count - _versusBots.Count))
                _versusRetiring[p.EntityHandle.Raw] = new("pending", p.Slot, p.UserId!.Value, p.EntityHandle.Raw);
        foreach (var b in _versusBots.Values) _versusRetiring[b.Serial] = b;
        foreach (var b in _versusRetiring.Values) if (VersusResolve(b) is { } p)
        {
            // Finish locks survivors after coordinator ownership has been released.
            // Explicitly release that hold before kicking, while identity is still valid.
            _api?.StopReplay(p.Slot);
            _api?.Unlock(p.Slot, LockKind.All); _api?.Unlock(p.Slot, LockKind.Aim); _api?.Unlock(p.Slot, LockKind.Weapon);
            Server.ExecuteCommand("kickid " + p.UserId!.Value);
        }
        if (_versusCvars != null) Server.ExecuteCommand("bot_quota " + Utilities.GetPlayers().Count(p => p.IsValid && p.IsBot && !_versusRetiring.ContainsKey(p.EntityHandle.Raw)));
        foreach (var (handle, name) in _versusTemporary)
        {
            var e = new CHandle<CBaseEntity>(handle).Value;
            if (e is not { IsValid: true } || e.DesignerName != name) continue;
            if (name.StartsWith("weapon_", StringComparison.Ordinal) && e.OwnerEntity.Value is { IsValid: true, DesignerName: "player" } pawn &&
                pawn.As<CCSPlayerPawn>().Controller.Value is { IsValid: true } holder && !_versusBots.Values.Any(b => b.Serial == holder.EntityHandle.Raw)) continue;
            e.Remove();
        }
        _versusTemporary.Clear(); _versusBots.Clear();
    }
    private void StopVersus(string reason, bool close = false)
    {
        bool active = _versusState != "idle";
        if (_versusState == "recording" && _versusOwner is { } owner) try { _api?.StopRecord(owner.Slot); } catch (Exception e) { Log(e.Message); }
        VersusPlayer()?.PrintToCenterHtml(" ", 1);
        _versusRecording = null;
        try { if (_versusCvars != null) TrackVersusEntities(); CleanupVersusRound(); }
        finally
        {
            _versusState = "idle";
            if (_versusCvars != null)
            {
                foreach (var (key, value) in _versusCvars) Server.ExecuteCommand(key + " " + value);
                _versusCvars = null; File.Delete(Path.Combine(_dataRoot, "versus-recovery.json"));
            }
            if (close) { _versusGeneration++; _versusIO = false; _versusOwner = null; _versusScene = null; _versusAnchors.Clear(); _versusRespawn.Clear(); }
        }
        if (active) VersusNotify(reason + "；已停止并恢复参数。");
    }
    private void VersusTick()
    {
        foreach (var (serial, bot) in _versusRetiring.ToArray()) if (VersusResolve(bot) == null) _versusRetiring.Remove(serial);
        if (!VersusLease) return;
        if (_versusOwner == null && _versusIO) return;
        try
        {
            var owner = VersusPlayer();
            if (_versusTestPhase == 0 && (owner == null || !VersusAuthorized(owner))) { StopVersus("操作者断线或权限失效", close: true); return; }
            if (_versusTestPhase != 0 && OtherHumans(0)) throw new InvalidOperationException("真人进入，诊断取消。");
            if (_versusState == "idle") return;
            if (_modeChanging || _trainingMode != "Practice" || DefenseLease || DefenseBusy) throw new InvalidOperationException("环境或模式已变化。");
            if (_versusState is "countdown" or "recording")
            {
                if (owner == null || !owner.PawnIsAlive || owner.TeamNum != VersusScene.Parse(_versusRecordingKey).Team) throw new InvalidOperationException("录制玩家死亡或切队，已保留原轨迹。");
                owner.PrintToCenterHtml($"<font color='{(_versusState == "recording" ? "#FF6666" : "#FFD166")}'>{Duration((_versusState == "recording" ? Server.TickCount - _versusRecordStart : _versusAt - Server.TickCount) * Server.TickInterval)}</font>", 1);
                if (_versusState == "countdown" && Server.TickCount >= _versusAt)
                {
                    int offset = _versusRecording!.OffsetMs;
                    _versusRecording = Inventory.Capture(owner, VersusScene.Parse(_versusRecordingKey).Role); _versusRecording.OffsetMs = offset;
                    if (!_api!.SetReplayPawn(owner.Slot, owner.PlayerPawn.Value!.Handle) || !_api.StartRecord(owner.Slot)) throw new InvalidOperationException("原生录制启动失败。");
                    _versusRecordStart = Server.TickCount; _versusState = "recording";
                }
                else if (_versusState == "recording" && Server.TickCount - _versusRecordStart >= MaxTicks) FinishVersusRecording();
                return;
            }
            if (VersusHasOutsiders()) throw new InvalidOperationException("真人进入 T／CT，模拟已取消，请转为观察者。");
            if (ConVar.Find("mp_respawn_on_death_t")!.GetPrimitiveValue<bool>() || ConVar.Find("mp_respawn_on_death_ct")!.GetPrimitiveValue<bool>())
                throw new InvalidOperationException("其他系统开启了自动复活，模拟已取消。");
            foreach (var bot in _versusBots.Values) if (VersusResolve(bot) is { } p) DefenseBotFunds(p);
            if (Server.TickCount % 16 == 0) TrackVersusEntities();
            switch (_versusState)
            {
                case "cleaning":
                    if (Server.TickCount > _versusDeadline) throw new TimeoutException("旧 BOT 清理超时。");
                    if (_versusRetiring.Count != 0) return;
                    if (_versusReadyAt == 0) { _versusReadyAt = Server.TickCount + 2; return; }
                    if (Server.TickCount >= _versusReadyAt) SpawnVersusRound();
                    break;
                case "preparing": PrepareVersusTick(); break;
                case "playing":
                    _versusCoordinator!.Tick(Server.TickCount);
                    string? result = _versusCoordinator.Result(Server.TickCount - _versusRoundStart >= DefenseTicks(_versusScene!.RoundSeconds * 1000));
                    if (result == null) _versusSettleAt = 0;
                    else if (result == "Timeout" || _versusSettleAt != 0 && Server.TickCount >= _versusSettleAt)
                    {
                        _versusLastResult = result; _versusRounds++;
                        VersusNotify($"第 {_versusRounds} 轮：{(result == "Draw" ? "平局" : result == "Timeout" ? "超时" : result + " 获胜")}；T 存活 {_versusCoordinator.Alive(2)}，CT 存活 {_versusCoordinator.Alive(3)}。");
                        _versusCoordinator.Cancel(); XRayClear();
                        foreach (var bot in _versusBots.Values) if (VersusResolve(bot) is { PawnIsAlive: true } p) _api!.Lock(p.Slot, LockKind.All);
                        _versusAt = Server.TickCount + DefenseTicks(3000); _versusState = "between";
                    }
                    else _versusSettleAt = Server.TickCount + 1;
                    break;
                case "between":
                    if (Server.TickCount >= _versusAt) { if (_versusScene!.Repeat) BeginVersusRound(); else StopVersus("本轮结束，循环关闭"); }
                    break;
            }
        }
        catch (Exception e) { _versusError = e.Message; VersusNotify("操作中止：" + e.Message); StopVersus("异常停止"); }
    }
    private void RecoverVersus()
    {
        string file = Path.Combine(_dataRoot, "versus-recovery.json"); if (!File.Exists(file)) return;
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? throw new InvalidDataException("KSV 恢复文件无效。");
        if (values.Keys.Any(k => !VersusScopedCvars.Contains(k)) || values.Values.Any(v => !System.Text.RegularExpressions.Regex.IsMatch(v, "^[a-zA-Z0-9_.+-]{1,32}$")))
            throw new InvalidDataException("KSV 恢复参数无效。");
        foreach (var (key, value) in values) Server.ExecuteCommand(key + " " + value);
        File.Delete(file);
    }
}
