using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using KeranStrat.Core;

namespace KeranStrat.Plugin;

public sealed partial class KeranStratPlugin
{
    private string _defenseState = "idle";
    private NativeReplayEngine? _defenseEngine;
    private DefenseBotController? _defenseController;
    private readonly List<(int Role, int Slot, nint Handle, int UserId)> _defenseBots = [];
    private readonly HashSet<uint> _defenseSpawnBaseline = [];
    private Dictionary<string, string>? _defenseCvars;
    private int _defenseDeadline, _defenseRoundStart, _defenseNextRound, _defenseReadyAt, _defenseSettlementAt, _defenseCleanupReady;
    private readonly Dictionary<uint, (int Slot, int UserId)> _defenseRetiring = [];
    private string _defenseStopReason = "none";
    private bool DefenseRetiring(CCSPlayerController p) => _defenseRetiring.TryGetValue(p.EntityHandle.Raw, out var old) && old.UserId == p.UserId;
    private readonly Dictionary<uint, string> _defenseTemporary = [];
    private readonly HashSet<uint> _defenseEntityBaseline = [];
    private readonly HashSet<uint> _defenseParticipantHandles = [];
    private readonly HashSet<uint> _defenseExternalBots = [];
    private int _defenseEntityScanAt;
    private string[] _defenseEntityClasses = DefenseTemporaryClasses;
    private static readonly string[] DefenseTemporaryClasses = ["smokegrenade_projectile", "flashbang_projectile", "hegrenade_projectile", "molotov_projectile", "decoy_projectile", "inferno", "weapon_ak47", "weapon_m4a1", "weapon_m4a1_silencer", "weapon_awp", "weapon_ssg08", "weapon_glock", "weapon_usp_silencer", "weapon_smokegrenade", "weapon_flashbang", "weapon_hegrenade", "weapon_molotov", "weapon_incgrenade", "weapon_decoy"];

    private CCSPlayerController? DefensePlayer()
    {
        return _defenseSession != null && _defenseSession.Members.TryGetValue(_defenseSession.OwnerId, out var owner) ? DefenseResolve(owner) : null;
    }
    private void StartDefense(CCSPlayerController player)
    {
        if (!Config.DefenseEnabled || DefenseBusy) throw new InvalidOperationException("Defense 当前不可启动。");
        DefenseRequireIdle();
        if (_defenseSession == null && DefenseDiagnostic) _defenseSession = new DefenseSession(DefenseCreateParticipant(player));
        if (_defenseSession == null || !_defenseSession.IsOwner(DefenseIdentityOf(player))) throw new InvalidOperationException("只有房主可以开始训练。");
        if (_trainingMode != "Practice" || !_practice.Active || _modeChanging || player.TeamNum != 3)
            throw new InvalidOperationException("需要 Practice 和 CT 阵营。");
        if (_defenseTactic == null || _defenseTactic.Map != Server.MapName || _defenseTactic.Team != 2 || _defenseTactic.Tracks.Count == 0 || _defenseSettings == null)
            throw new InvalidOperationException("先 !ksd load T方战术，再 !ksd position 设置 CT 防守位置。");
        _defenseSettings.MigrateOwner(_defenseSession.OwnerId);
        _defenseTactic.Validate(MaxTicks); _defenseSettings.Validate(_defenseTactic);
        if (!Bind()) throw new InvalidOperationException("BotController 未就绪。");
        foreach (var member in _defenseSession.Members.Values)
        {
            var p = DefenseResolve(member);
            if (p == null || p.TeamNum != 3 || !DefenseAuthorized(p)) throw new InvalidOperationException(member.Name + " 未就绪或权限失效。");
            if (!_defenseSettings.Defenders.TryGetValue(member.Identity.SteamId, out var saved)) throw new InvalidOperationException(member.Name + " 尚未 !ksd position 保存防守位置。");
            if (p.PawnIsAlive)
            {
                var equipment = Inventory.Capture(p, 1); equipment.Initial = saved.Initial; equipment.Health = 100;
                _defenseSettings.Defenders[member.Identity.SteamId] = equipment;
            }
        }
        var outsiders = DefenseOutsiders();
        if (outsiders.Length > 0) throw new InvalidOperationException("请未加入队伍的玩家转为观察者：" + string.Join('、', outsiders.Select(p => p.PlayerName)));
        _defenseOwner = player.SteamID; _defenseOwnerSlot = player.Slot;
        _modeDefense = true;
        _defenseSettings.Defender = _defenseSettings.Defenders[_defenseSession.OwnerId]; DefenseSaveSettings();
        var values = new Dictionary<string, string>();
        foreach (string key in new[] { "bot_dont_shoot", "bot_difficulty", "bot_stop", "bot_zombie", "bot_quota", "bot_quota_mode", "mp_respawn_immunitytime", "mp_respawn_on_death_ct", "mp_respawn_on_death_t" })
            values[key] = ConVar.Find(key)?.StringValue ?? throw new InvalidOperationException("缺少 Defense 参数 " + key);
        string recovery = Path.Combine(_dataRoot, "defense-recovery.json");
        File.WriteAllText(recovery + ".tmp", JsonSerializer.Serialize(values)); File.Move(recovery + ".tmp", recovery, true);
        _defenseCvars = values; _defenseEngine = new NativeReplayEngine(_api!);
        _defenseExternalBots.Clear();
        foreach (var bot in Utilities.GetPlayers()) if (bot.IsValid && bot.IsBot && !DefenseRetiring(bot)) _defenseExternalBots.Add(bot.EntityHandle.Raw);
        _defenseController = new DefenseBotController(_defenseEngine);
        _defenseError = "none";
        try
        {
            Server.ExecuteCommand("bot_dont_shoot 0"); Server.ExecuteCommand("bot_difficulty " + (_defenseSettings.Difficulty - 1));
            Server.ExecuteCommand("bot_stop 0"); Server.ExecuteCommand("bot_zombie 0");
            Server.ExecuteCommand("bot_quota_mode normal"); Server.ExecuteCommand("mp_respawn_immunitytime 0");
            Server.ExecuteCommand("mp_respawn_on_death_ct 0"); Server.ExecuteCommand("mp_respawn_on_death_t 0");
            BeginDefenseRound();
        }
        catch { StopDefense("启动失败"); throw; }
    }
    private void BeginDefenseRound()
    {
        CleanupDefenseBots(); CleanupDefenseEntities();
        _defenseState = "cleaning"; _defenseCleanupReady = 0; _defenseDeadline = Server.TickCount + DefenseTicks(10000);
    }
    private void RespawnDefenseRound()
    {
        if (_defenseSession == null || _defenseSession.Members.Count == 0) throw new InvalidOperationException("没有有效 CT 成员。");
        foreach (var member in _defenseSession.Members.Values.Where(m => m.PendingLeave).ToArray())
            if (DefenseResolve(member) is { IsValid: true } leaving) leaving.ChangeTeam(CsTeam.Spectator);
        _defenseSession.ApplyPendingLeaves();
        _defenseSession.BeginRound();
        _defenseSettlementAt = 0;
        _defenseState = "respawning"; _defenseDeadline = Server.TickCount + DefenseTicks(10000);
        foreach (var member in _defenseSession.Round.Values)
        {
            var p = DefenseResolve(member) ?? throw new InvalidOperationException(member.Name + " 已离线。");
            if (p.TeamNum != 3) throw new InvalidOperationException(member.Name + " 已离开 CT。");
            if (!p.PawnIsAlive) p.Respawn();
        }
    }
    private void RestoreDefenseRound()
    {
        if (_defenseSession == null) throw new InvalidOperationException("防守会话不存在。");
        _defenseEntityBaseline.Clear();
        _defenseParticipantHandles.Clear();
        _defenseEntityClasses = DefenseTemporaryClasses.Concat(_defenseTactic!.Tracks.Values.SelectMany(t => t.Equipment).Select(e => e.ClassName))
            .Concat(_defenseSession.Round.Keys.SelectMany(id => _defenseSettings!.Defenders[id].Equipment).Select(e => e.ClassName)).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var name in _defenseEntityClasses)
            foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(name)) _defenseEntityBaseline.Add(entity.EntityHandle.Raw);
        foreach (var member in _defenseSession.Round.Values)
        {
            var player = DefenseResolve(member) ?? throw new InvalidOperationException(member.Name + " 身份已变化。");
            if (!player.PawnIsAlive || player.PlayerPawn.Value == null) throw new InvalidOperationException(member.Name + " 在恢复配装前死亡，请重新开始。");
            var saved = _defenseSettings!.Defenders[member.Identity.SteamId]; saved.Health = 100;
            Inventory.Restore(player, saved);
            if (player.PlayerPawn.Value != null) player.PlayerPawn.Value.GunGameImmunity = false;
            _defenseParticipantHandles.Add(player.EntityHandle.Raw);
            if (player.PlayerPawn.Value?.WeaponServices is { } weapons)
                foreach (var handle in weapons.MyWeapons)
                    if (handle.Value is { IsValid: true } item) _defenseTemporary[item.EntityHandle.Raw] = item.DesignerName;
        }
        _defenseSpawnBaseline.Clear();
        foreach (var p in Utilities.GetPlayers()) if (p.IsValid) _defenseSpawnBaseline.Add(p.EntityHandle.Raw);
        _defenseBots.Clear();
        foreach (int role in _defenseTactic!.Tracks.Keys.Order())
        { _defenseBots.Add((role, -1, 0, -1)); Server.ExecuteCommand("bot_add_t"); }
        Server.ExecuteCommand("bot_quota " + (_defenseBots.Count + Utilities.GetPlayers().Count(p => p.IsValid && p.IsBot && _defenseExternalBots.Contains(p.EntityHandle.Raw))));
        _defenseReadyAt = 0; _defenseDeadline = Server.TickCount + DefenseTicks(15000); _defenseState = "preparing";
        DefenseNotify("准备第 " + (_defenseStats.Rounds + 1) + " 轮，正在生成 T 方 BOT。");
    }
    private static int DefenseTicks(int milliseconds) => checked((int)Math.Ceiling(milliseconds / (1000d * Server.TickInterval)));
    private void DefensePrepareTick()
    {
        if (Server.TickCount > _defenseDeadline) throw new TimeoutException("Defense BOT 创建或出生超时。");
        if (_defenseReadyAt != 0)
        {
            if (Server.TickCount < _defenseReadyAt) return;
            int start = Server.TickCount + 1;
            _defenseController!.Begin(start, _defenseBots.Select(b =>
            {
                var track = _defenseTactic!.Tracks[b.Role];
                return new DefenseBotEntry(b.Role, b.Slot, DefenseTicks(track.OffsetMs), track.Ticks.Length,
                    DefenseBotController.HandoffTicks(_defenseSettings!.HandoffMilliseconds.GetValueOrDefault(b.Role), track, _defenseTactic.TickInterval),
                    _defenseSettings.HandoffMilliseconds.GetValueOrDefault(b.Role) == null);
            }).ToArray());
            _defenseRoundStart = start; _defenseState = "running"; DefenseNotify("开始防守！"); return;
        }
        var players = Utilities.GetPlayers();
        for (int i = 0; i < _defenseBots.Count; i++)
        {
            var b = _defenseBots[i]; if (b.Slot >= 0) continue;
            var bot = players.FirstOrDefault(p => p.IsValid && p.IsBot && p.TeamNum == 2 && p.UserId != null && !_defenseSpawnBaseline.Contains(p.EntityHandle.Raw));
            if (bot == null) continue;
            if (!bot.PawnIsAlive) { bot.Respawn(); continue; }
            _defenseSpawnBaseline.Add(bot.EntityHandle.Raw); _defenseBots[i] = (b.Role, bot.Slot, bot.Handle, bot.UserId!.Value);
            _defenseParticipantHandles.Add(bot.EntityHandle.Raw);
            DefenseBotFunds(bot);
            _defenseEngine!.Prepare(bot, _defenseTactic!.Tracks[b.Role]);
        }
        if (_defenseBots.All(b => b.Slot >= 0))
        { _defenseReadyAt = Server.TickCount + DefenseTicks(3000); DefenseNotify("3 秒后开始防守。"); }
    }
    private void DefenseTick()
    {
        DefenseValidateMembers();
        if (_defenseState == "idle") return;
        try
        {
            var player = DefensePlayer();
            if (player == null || player.TeamNum != 3) throw new InvalidOperationException("防守玩家离线或离开 CT 队伍。");
            if (!Config.DefenseEnabled || (!player.IsBot && !AccessPolicy.Allows(true, _defenseAllowed, player.AuthorizedSteamID?.SteamId64.ToString())))
                throw new InvalidOperationException("Defense 已关闭或权限已移除。");
            if (ConVar.Find("mp_respawn_on_death_ct")?.GetPrimitiveValue<bool>() != false || ConVar.Find("mp_respawn_on_death_t")?.GetPrimitiveValue<bool>() != false)
                throw new InvalidOperationException("自动复活参数被其他系统修改，请停止冲突的自动复活功能后重试。");
            if (DefenseOutsiders().Length > 0) { StopDefense("发现未入队真人进入 T/CT，请其转为观察者后由房主重新开始（本轮不计成绩）"); return; }
            if (_defenseSession!.Round.Count == 0 && _defenseState != "cleaning") { StopDefense("全部 CT 已离开，本轮不计成绩"); return; }
            foreach (var bot in _defenseBots)
                if (NativeReplayEngine.Player(bot.Slot) is { IsValid: true } current && current.Handle == bot.Handle && current.UserId == bot.UserId) DefenseBotFunds(current);
            if (Server.TickCount >= _defenseEntityScanAt)
            { TrackDefenseEntities(); _defenseEntityScanAt = Server.TickCount + DefenseTicks(200); }
            switch (_defenseState)
            {
                case "cleaning":
                    foreach (var (serial, old) in _defenseRetiring.ToArray())
                        if (NativeReplayEngine.Player(old.Slot) is not { IsValid: true } retiring || retiring.UserId != old.UserId || retiring.EntityHandle.Raw != serial)
                            _defenseRetiring.Remove(serial);
                    if (Server.TickCount > _defenseDeadline) throw new TimeoutException("上一轮 BOT 断开清理超时，请检查引擎状态。");
                    if (_defenseRetiring.Count != 0) break;
                    if (_defenseCleanupReady == 0) { _defenseCleanupReady = Server.TickCount + 2; break; }
                    if (Server.TickCount < _defenseCleanupReady) break;
                    Server.ExecuteCommand("bot_quota " + Utilities.GetPlayers().Count(p => p.IsValid && p.IsBot && _defenseExternalBots.Contains(p.EntityHandle.Raw)));
                    RespawnDefenseRound();
                    break;
                case "respawning":
                    var waiting = _defenseSession.Round.Values.Where(m => DefenseResolve(m) is not { PawnIsAlive: true }).ToArray();
                    if (Server.TickCount > _defenseDeadline) throw new TimeoutException("CT 复活超时：" + string.Join('、', waiting.Select(m => m.Name)));
                    if (waiting.Length == 0) _defenseState = "restoring";
                    break;
                case "restoring": RestoreDefenseRound(); break;
                case "preparing":
                    if (_defenseSession.Round.Values.Any(m => DefenseResolve(m) is not { PawnIsAlive: true })) throw new InvalidOperationException("准备期间 CT 死亡，请重新开始。");
                    DefensePrepareTick(); break;
                case "running":
                    _defenseController!.Tick(Server.TickCount);
                    foreach (var member in _defenseSession.Round.Values) if (DefenseResolve(member) is not { PawnIsAlive: true }) member.Dead = true;
                    var outcome = DefenseStatistics.EvaluateTeam(_defenseSession.Round.Values.Count(m => !m.Dead), _defenseSession.Round.Count, _defenseController.DeadCount, _defenseBots.Count,
                        Server.TickCount - _defenseRoundStart >= DefenseTicks(_defenseSettings!.RoundSeconds * 1000));
                    if (outcome == null) _defenseSettlementAt = 0;
                    else if (outcome == DefenseResult.Timeout || _defenseSettlementAt != 0 && Server.TickCount >= _defenseSettlementAt)
                        FinishDefenseRound(outcome.Value);
                    else _defenseSettlementAt = Server.TickCount + 1;
                    break;
                case "between":
                    if (Server.TickCount >= _defenseNextRound)
                    { if (_defenseSettings!.Repeat) BeginDefenseRound(); else StopDefense("本轮结束，自动循环关闭"); }
                    break;
            }
        }
        catch (Exception e) { _defenseError = e.Message; DefenseNotify("训练中止：" + e.Message); StopDefense("异常停止"); }
    }
    private void FinishDefenseRound(DefenseResult result)
    {
        _defenseStats.Finish(result);
        string label = result == DefenseResult.Success ? "防守成功" : result == DefenseResult.Failure ? "防守失败" : result == DefenseResult.Draw ? "平局" : "训练超时";
        DefenseNotify($"第 {_defenseStats.Rounds} 轮结束：{label}；CT 存活 {_defenseSession!.Round.Values.Count(m => !m.Dead)}/{_defenseSession.Round.Count}；T BOT 击杀 {_defenseController!.DeadCount}/{_defenseBots.Count}；用时 {(Server.TickCount - _defenseRoundStart) * Server.TickInterval:F1} 秒。");
        DefenseNotify($"总计 {_defenseStats.Rounds}，成功 {_defenseStats.Success}，失败 {_defenseStats.Failure}，超时 {_defenseStats.Timeouts}，平局 {_defenseStats.Draws}。3 秒后重置。");
        _defenseController.Cancel(); // stop AI immediately so the finished round cannot keep hurting CT
        _defenseNextRound = Server.TickCount + DefenseTicks(3000); _defenseState = "between";
        foreach (var b in _defenseBots) if (NativeReplayEngine.Player(b.Slot) is { IsValid: true } p && p.Handle == b.Handle && p.UserId == b.UserId) _api?.Lock(b.Slot, BotControllerApi.LockKind.All);
    }
    private void TrackDefenseEntities()
    {
        foreach (var name in _defenseEntityClasses)
            foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(name))
            {
                uint serial = entity.EntityHandle.Raw;
                if (_defenseEntityBaseline.Contains(serial) || _defenseTemporary.ContainsKey(serial)) continue;
                var ownerPawn = entity.OwnerEntity.Value;
                var owner = ownerPawn is { IsValid: true, DesignerName: "player" } ? ownerPawn.As<CCSPlayerPawn>().Controller.Value : null;
                if (owner == null && name.EndsWith("_projectile", StringComparison.Ordinal)) owner = entity.As<CBaseGrenade>().Thrower.Value?.Controller.Value;
                if (owner == null && ownerPawn is { IsValid: true } projectile && projectile.DesignerName.EndsWith("_projectile", StringComparison.Ordinal))
                    owner = projectile.As<CBaseGrenade>().Thrower.Value?.Controller.Value;
                // Never delete unrelated entities: require the owner to be one of this round's participants.
                if (owner != null && _defenseParticipantHandles.Contains(owner.EntityHandle.Raw)) _defenseTemporary[serial] = name;
            }
    }
    private void CleanupDefenseEntities()
    {
        foreach (var (handle, name) in _defenseTemporary)
        {
            var entity = new CHandle<CBaseEntity>(handle).Value;
            if (entity is not { IsValid: true } || entity.DesignerName != name) continue;
            // Never strip a weapon currently held by a human, including someone who just left.
            if (name.StartsWith("weapon_", StringComparison.Ordinal) && entity.OwnerEntity.Value is { IsValid: true, DesignerName: "player" } pawn &&
                pawn.As<CCSPlayerPawn>().Controller.Value is { IsValid: true } holder &&
                (!holder.As<CCSPlayerController>().IsBot || !_defenseParticipantHandles.Contains(holder.EntityHandle.Raw))) continue;
            entity.Remove();
        }
        _defenseTemporary.Clear(); _defenseEntityBaseline.Clear();
    }
    private void CleanupDefenseBots()
    {
        if (_defenseBots.Count == 0) return;
        _defenseController?.Cancel();
        var owned = _defenseBots.Where(b => b.Slot >= 0).ToList();
        if (_defenseState == "preparing")
            foreach (var bot in Utilities.GetPlayers().Where(p => p.IsValid && p.IsBot && p.TeamNum == 2 && p.UserId != null && !_defenseSpawnBaseline.Contains(p.EntityHandle.Raw)).Take(_defenseBots.Count(b => b.Slot < 0)))
                if (!owned.Any(b => b.Handle == bot.Handle)) owned.Add((0, bot.Slot, bot.Handle, bot.UserId!.Value));
        int otherBots = Utilities.GetPlayers().Count(p => p.IsValid && p.IsBot && !owned.Any(b => b.Handle == p.Handle));
        foreach (var b in owned)
        {
            var bot = NativeReplayEngine.Player(b.Slot);
            if (bot is not { IsValid: true, IsBot: true } || bot.Handle != b.Handle || bot.UserId != b.UserId) continue;
            _defenseRetiring[bot.EntityHandle.Raw] = (bot.Slot, b.UserId);
            try { _defenseEngine?.Stop(b.Slot); _api?.Unlock(b.Slot, BotControllerApi.LockKind.All); }
            catch (Exception e) { _defenseError = "BOT 解锁清理失败：" + e.Message; DefenseNotify(_defenseError); }
            if (bot.UserId is int id) Server.ExecuteCommand("kickid " + id);
        }
        // Kick only verified owned identities first; lower quota after those explicit removals.
        Server.ExecuteCommand("bot_quota " + otherBots);
        _defenseBots.Clear();
    }
    private void StopDefense(string reason)
    {
        _defenseStopReason = reason;
        bool wasActive = DefenseBusy;
        bool clean = true;
        _defenseGeneration++; _defenseLoading = false;
        if (_defenseCvars != null)
        {
            try { TrackDefenseEntities(); } catch (Exception e) { clean = false; _defenseError = "实体追踪清理警告：" + e.Message; DefenseNotify(_defenseError); }
            try { CleanupDefenseBots(); } catch (Exception e) { clean = false; _defenseError = "BOT 清理警告：" + e.Message; DefenseNotify(_defenseError); }
            try { CleanupDefenseEntities(); } catch (Exception e) { clean = false; _defenseError = "临时实体清理警告：" + e.Message; DefenseNotify(_defenseError); }
        }
        _defenseState = "idle";
        if (_defenseCvars != null)
        {
            foreach (var (key, value) in _defenseCvars) Server.ExecuteCommand(key + " " + value);
            _defenseCvars = null;
            string file = Path.Combine(_dataRoot, "defense-recovery.json"); if (File.Exists(file)) File.Delete(file);
        }
        if (wasActive) DefenseNotify(reason + (clean ? "；已清理 Defense BOT 并恢复训练参数。" : "；部分清理失败，请检查错误日志。"));
    }
    private void RecoverDefenseEnvironment()
    {
        string file = Path.Combine(_dataRoot, "defense-recovery.json"); if (!File.Exists(file)) return;
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? throw new InvalidDataException("Defense 恢复文件无效。");
        string[] allowed = ["bot_dont_shoot", "bot_difficulty", "bot_stop", "bot_zombie", "bot_quota", "bot_quota_mode", "mp_respawn_immunitytime", "mp_respawn_on_death_ct", "mp_respawn_on_death_t"];
        if (values.Keys.Any(k => !allowed.Contains(k)) || values.Values.Any(v => !System.Text.RegularExpressions.Regex.IsMatch(v, "^[a-zA-Z0-9_.+-]{1,32}$"))) throw new InvalidDataException("Defense 恢复文件不安全。");
        foreach (var (key, value) in values) Server.ExecuteCommand(key + " " + value);
        File.Delete(file);
    }
}
