using KeranStrat.Core;
using System.Text.Json;

static class DefenseTests
{
    public static void Run(Action<string, Action> test, Action<bool, string> assert, Action<Action> reject, Func<Tactic> fixture, string temp)
    {
        void Check(bool value) => assert(value, "Defense assertion");
        test("Defense sparse roles hand off on independent local timelines and retain combat bots", () =>
        {
            var engine = new DefenseFake(); var controller = new DefenseBotController(engine);
            controller.Begin(100, [new(1, 1, 0, 100, 10), new(3, 3, 5, 100, 20), new(5, 5, 10, 100, 100)]);
            for (int tick = 100; tick <= 210; tick++) { engine.Now = tick; controller.Tick(tick); }
            Check(engine.Starts.SequenceEqual(new[] { (1, 100), (3, 105), (5, 110) }));
            Check(engine.Handoffs.SequenceEqual(new[] { (1, 110), (3, 125), (5, 210) }));
            Check(controller.Entries.All(e => e.State == DefenseBotState.CombatAI) && engine.Stops.Count == 0);
            controller.Cancel(); Check(engine.Stops.Count == 3);
        });
        test("Defense supports exactly one through five attackers without padding", () =>
        {
            for (int count = 1; count <= 5; count++)
            {
                var engine = new DefenseFake(); var controller = new DefenseBotController(engine);
                controller.Begin(0, Enumerable.Range(1, count).Select(i => new DefenseBotEntry(i, i, (i - 1) * 3, 64, 64)).ToArray());
                for (int tick = 0; tick <= 80; tick++) { engine.Now = tick; controller.Tick(tick); }
                Check(engine.Starts.Count == count && engine.Handoffs.Count == count && engine.Stops.Count == 0);
                controller.Cancel(); Check(engine.Stops.Count == count);
            }
        });
        test("Defense auto waits for the actual native end and rejects a stuck buffer", () =>
        {
            var engine = new DefenseFake(); var controller = new DefenseBotController(engine);
            controller.Begin(0, [new(1, 1, 0, 64, 64, true)]);
            for (int tick = 0; tick < 100; tick++) { engine.Now = tick; controller.Tick(tick); }
            Check(engine.Handoffs.Count == 0);
            engine.Now = 100; controller.Tick(100); Check(engine.Handoffs.Single().Tick == 100); controller.Cancel();
            engine.NeverEnds = true; engine.Now = 200; controller.Begin(200, [new(1, 1, 0, 64, 64, true)]); controller.Tick(200);
            engine.Now = 393; reject(() => controller.Tick(393)); controller.Cancel();
        });
        test("Defense deaths preserve other replays; disconnection is a distinct error", () =>
        {
            var engine = new DefenseFake(); var controller = new DefenseBotController(engine);
            controller.Begin(0, [new(1, 1, 0, 100, 80), new(3, 3, 0, 100, 90)]);
            controller.Tick(0); engine.Dead.Add(1); engine.Now = 5; controller.Tick(5);
            Check(controller.DeadCount == 1 && controller.Entries[1].State == DefenseBotState.Replaying);
            engine.Disconnected.Add(3); reject(() => controller.Tick(6)); controller.Cancel(); Check(engine.Stops.Contains(3));
        });
        test("Defense rejects early replay termination and failed AI handoff", () =>
        {
            var engine = new DefenseFake(); var controller = new DefenseBotController(engine);
            controller.Begin(0, [new(1, 1, 0, 100, 50)]); controller.Tick(0);
            engine.Stopped.Add(1); engine.Now = 10; reject(() => controller.Tick(10)); controller.Cancel();
            engine.Stopped.Clear(); engine.FailHandoff = true; controller.Begin(20, [new(1, 1, 0, 100, 0)]);
            engine.Now = 20; reject(() => controller.Tick(20)); controller.Cancel();
        });
        test("native death events settle before lagging pawn-alive polling and do not interrupt other bots", () =>
        {
            var engine = new DefenseFake(); var controller = new DefenseBotController(engine);
            controller.Begin(0, [new(1, 1, 0, 100, 80), new(3, 3, 0, 100, 90)]); controller.Tick(0);
            controller.MarkDead(1); controller.MarkDead(99); engine.Now = 1; controller.Tick(1);
            Check(controller.DeadCount == 1 && engine.Stops.SetEquals(new[] { 1 }) && controller.Entries[1].State == DefenseBotState.Replaying);
            controller.Cancel(); controller.Begin(10, [new(1, 1, 0, 100, 80)]); engine.Now = 10; controller.Tick(10);
            Check(controller.DeadCount == 0);
        });
        test("Defense handoff bounds, auto timing and separate SteamID whitelist", () =>
        {
            var tactic = fixture(); var track = tactic.Tracks[1];
            Check(DefenseBotController.HandoffTicks(null, track, tactic.TickInterval) == 64);
            Check(DefenseBotController.HandoffTicks(300, track, tactic.TickInterval) == 20);
            reject(() => DefenseBotController.HandoffTicks(1001, track, tactic.TickInterval));
            reject(() => DefenseBotController.HandoffTicks(-1, track, tactic.TickInterval));
            string a = "76561198000000000", b = "76561198000000001";
            var original = new HashSet<string> { a }; var defense = new HashSet<string> { b };
            Check(AccessPolicy.Allows(true, original, a) && !AccessPolicy.Allows(true, defense, a));
            Check(!AccessPolicy.Allows(true, original, b) && AccessPolicy.Allows(true, defense, b));
            Check(!AccessPolicy.Allows(false, defense, b) && AccessPolicy.Allows(true, original, a));
            Check(!AccessPolicy.Allows(true, new HashSet<string>(), b) && !AccessPolicy.Allows(true, defense, null));
        });
        test("Defense settings and scores do not rewrite original tactic archives", () =>
        {
            var tactic = fixture(); var store = new FileTacticStore(Path.Combine(temp, "defense-readonly")); store.Save(tactic);
            string file = Directory.GetFiles(Path.Combine(temp, "defense-readonly"), "*.ks", SearchOption.AllDirectories).Single();
            var bytes = File.ReadAllBytes(file);
            var settings = new DefenseSettings { TacticId = tactic.Id, Map = tactic.Map, Defender = tactic.Tracks[1], HandoffMilliseconds = new() { [1] = 300 } };
            JsonSerializer.Deserialize<DefenseSettings>(JsonSerializer.Serialize(settings))!.Validate(store.Load(tactic.Map, tactic.Name));
            Check(File.ReadAllBytes(file).SequenceEqual(bytes)); tactic.Team = 3; reject(() => settings.Validate(tactic));
            var stats = new DefenseStatistics(); foreach (var result in Enum.GetValues<DefenseResult>()) stats.Finish(result);
            Check(stats.Rounds == 4 && stats.Success == 1 && stats.Failure == 1 && stats.Timeouts == 1 && stats.Draws == 1);
            Check(DefenseStatistics.Evaluate(false, 0, 3, false) == DefenseResult.Failure);
            Check(DefenseStatistics.Evaluate(true, 3, 3, false) == DefenseResult.Success);
            Check(DefenseStatistics.Evaluate(true, 1, 3, true) == DefenseResult.Timeout);
            Check(DefenseStatistics.Evaluate(true, 1, 3, false) == null);
        });
        test("cooperative outcomes count only effective CT and resolve simultaneous elimination deterministically", () =>
        {
            Check(DefenseStatistics.EvaluateTeam(1, 2, 0, 5, false) == null);
            Check(DefenseStatistics.EvaluateTeam(1, 2, 5, 5, false) == DefenseResult.Success);
            Check(DefenseStatistics.EvaluateTeam(0, 2, 4, 5, false) == DefenseResult.Failure);
            Check(DefenseStatistics.EvaluateTeam(0, 2, 5, 5, true) == DefenseResult.Draw);
            Check(DefenseStatistics.EvaluateTeam(2, 2, 3, 5, true) == DefenseResult.Timeout);
            Check(DefenseStatistics.EvaluateTeam(0, 0, 0, 5, true) == null);
            foreach (int count in new[] { 1, 2, 3, 5 }) Check(DefenseStatistics.EvaluateTeam(count, count, 5, 5, false) == DefenseResult.Success);
        });
        test("cooperative membership is explicit, capped at five and cannot transfer owner via slot reuse", () =>
        {
            DefenseParticipant Member(int i, int slot = -1) => new(new((76561198000000000UL + (uint)i).ToString(), slot < 0 ? i : slot, i, (uint)(100 + i)), "CT" + i);
            var owner = Member(1); var session = new DefenseSession(owner);
            Check(session.IsOwner(owner.Identity));
            Check(!session.IsOwner(owner.Identity with { UserId = 99 }));
            Check(!session.IsOwner(owner.Identity with { Handle = 999 }));
            Check(!session.IsOwner(Member(2, 1).Identity));
            for (int i = 2; i <= 5; i++) Check(session.Join(Member(i)));
            Check(!session.Join(Member(2))); reject(() => session.Join(Member(6)));
            Check(session.OwnerId == owner.Identity.SteamId && session.Members.Count == 5);
            var reused = Member(7, 2); Check(!session.Contains(reused.Identity) && !session.Remove(reused.Identity));
            var old = Member(2); Check(session.Remove(old.Identity)); Check(session.Join(reused));
            Check(!session.Contains(old.Identity) && session.Contains(reused.Identity));
        });
        test("cooperative round snapshots isolate joins, sticky deaths and deferred departures", () =>
        {
            var a = new DefenseParticipant(new("76561198000000001", 1, 11, 111), "Owner");
            var b = new DefenseParticipant(new("76561198000000002", 2, 12, 112), "Friend");
            var c = new DefenseParticipant(new("76561198000000003", 3, 13, 113), "Later");
            var session = new DefenseSession(a); session.Join(b); session.BeginRound();
            a.Dead = true; Check(session.Round.Values.Count(m => !m.Dead) == 1);
            session.Join(c); Check(session.Round.Count == 2 && session.Members.Count == 3);
            b.PendingLeave = true; Check(session.Round.ContainsKey(b.Identity.SteamId));
            session.ApplyPendingLeaves(); Check(session.Round.Count == 1 && !session.Members.ContainsKey(b.Identity.SteamId));
            session.BeginRound(); Check(session.Round.Count == 2 && session.Round.Values.All(m => !m.Dead));
            session.Remove(c.Identity); Check(session.Round.Count == 1 && session.OwnerId == a.Identity.SteamId);
        });
        test("legacy single-defender JSON migrates only owner and preserves independent multiplayer loadouts", () =>
        {
            var tactic = fixture(); string a = "76561198000000001", b = "76561198000000002";
            var old = new DefenseSettings { TacticId = tactic.Id, Map = tactic.Map, Defender = tactic.Tracks[1], Difficulty = 3, Repeat = false, RoundSeconds = 60, HandoffMilliseconds = new() { [1] = 300 } };
            var settings = JsonSerializer.Deserialize<DefenseSettings>(JsonSerializer.Serialize(old))!;
            settings.MigrateOwner(a); Check(settings.Defenders.Count == 1 && !settings.Defenders.ContainsKey(b));
            settings.Defenders[b] = tactic.Tracks[2]; settings.Validate(tactic);
            var again = JsonSerializer.Deserialize<DefenseSettings>(JsonSerializer.Serialize(settings))!; again.MigrateOwner(a); again.Validate(tactic);
            Check(again.Difficulty == 3 && !again.Repeat && again.RoundSeconds == 60 && again.HandoffMilliseconds[1] == 300);
            Check(again.Defenders[a].Role == 1 && again.Defenders[b].Role == 2);
            again.Defenders["not-authenticated"] = tactic.Tracks[1]; reject(() => again.Validate(tactic));
        });
    }
}

sealed class DefenseFake : IDefenseReplayEngine
{
    public int Now;
    public bool FailHandoff;
    public bool NeverEnds;
    public List<(int Slot, int Tick)> Starts { get; } = [];
    public List<(int Slot, int Tick)> Handoffs { get; } = [];
    public HashSet<int> Stops { get; } = [];
    public HashSet<int> Dead { get; } = [];
    public HashSet<int> Disconnected { get; } = [];
    public HashSet<int> Stopped { get; } = [];
    public bool Start(int slot) { Starts.Add((slot, Now)); return true; }
    public void Stop(int slot) => Stops.Add(slot);
    public bool IsConnected(int slot) => !Disconnected.Contains(slot);
    public bool IsAlive(int slot) => !Dead.Contains(slot);
    public bool IsPlaying(int slot) => !Stopped.Contains(slot) && (NeverEnds || Cursor(slot) < 100);
    public int Cursor(int slot) => Now - Starts.LastOrDefault(s => s.Slot == slot).Tick;
    public bool HandoffToCombatAI(int slot) { if (FailHandoff) return false; Handoffs.Add((slot, Now)); return true; }
}
