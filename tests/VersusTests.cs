using KeranStrat.Core;
using System.Runtime.InteropServices;
using System.IO.Compression;

static class VersusTests
{
    public static void Run(Action<string, Action> test, Action<bool, string> check, Action<Action> reject, Func<Tactic> fixture, string temp)
    {
        void Check(bool value) => check(value, "KSV assertion");
        VersusScene Scene()
        {
            var source = fixture(); var scene = new VersusScene { Name = "KSV 5v5", Map = source.Map, GameVersion = source.GameVersion };
            foreach (int team in new[] { 2, 3 }) foreach (var (role, track) in source.Snapshot().Tracks) scene.Tracks[VersusScene.Key(team, role)] = track;
            return scene;
        }
        test("KSV native archive roundtrip is independent from KS and retains ten distinct role buffers", () =>
        {
            string root = Path.Combine(temp, "versus"); var store = new VersusStore(root); var original = Scene();
            original.Handoff["T1"] = 250; original.Handoff["CT1"] = null; original.Tracks["CT1"].Equipment = [new("weapon_awp", 4, 20)];
            store.Save(original); var loaded = store.Load(original.Map, original.Name); loaded.Validate(playable: true);
            Check(loaded.Tracks.Count == 10 && loaded.Tracks["CT1"].Equipment[0].ClassName == "weapon_awp");
            Check(loaded.Handoff["T1"] == 250 && loaded.Handoff["CT1"] == null);
            foreach (var (key, track) in original.Tracks)
            {
                Check(MemoryMarshal.AsBytes(track.Ticks.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(loaded.Tracks[key].Ticks.AsSpan())));
                Check(MemoryMarshal.AsBytes(track.Subticks.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(loaded.Tracks[key].Subticks.AsSpan())));
                Check(MemoryMarshal.AsBytes(track.Commands.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(loaded.Tracks[key].Commands.AsSpan())));
            }
            Check(new FileTacticStore(root).List(original.Map).Count == 0);
            var ks = fixture(); ks.Name = original.Name; new FileTacticStore(root).Save(ks);
            Check(store.List(original.Map).Count == 1); reject(() => store.Load(original.Map, ks.Id.ToString()));
            reject(() => store.EnsureNameAvailable(original.Map, original.Name.ToLowerInvariant()));
            reject(() => store.Load(original.Map, "../original.ks"));
            var before = File.ReadAllBytes(Directory.GetFiles(root, "*.ks", SearchOption.AllDirectories).Single());
            store.Delete(original.Map, original.Name);
            Check(before.SequenceEqual(File.ReadAllBytes(Directory.GetFiles(root, "*.ks", SearchOption.AllDirectories).Single())));
        });
        test("KSV validates team-role namespace, side counts, native ABI, playable sides and handoff limits", () =>
        {
            var s = Scene(); s.Validate(playable: true);
            s.Tracks.Remove("T2"); s.Tracks.Remove("T4"); s.Tracks.Remove("CT1"); s.Tracks.Remove("CT3"); s.Tracks.Remove("CT5"); s.Validate(playable: true);
            Check(s.Tracks.Count == 5 && s.Tracks.ContainsKey("T1") && s.Tracks.ContainsKey("CT2"));
            s.Handoff["T1"] = 99999; reject(() => s.Validate()); s.Handoff.Clear();
            s.Kind = "KeranStrat"; reject(() => s.Validate()); s.Kind = "KeranStrat.Versus";
            s.CommandSize++; reject(() => s.Validate()); s.CommandSize--;
            s.Tracks["T6"] = fixture().Tracks[1]; reject(() => s.Validate()); s.Tracks.Remove("T6");
            foreach (var key in s.Tracks.Keys.Where(k => k.StartsWith("CT")).ToArray()) s.Tracks.Remove(key);
            s.Validate(); reject(() => s.Validate(playable: true));
        });
        test("KSV strict shares one clock and holds finished roles without AI handoff", () =>
        {
            var engine = new VersusFake(); var c = new VersusCoordinator(engine);
            c.Begin(100, [new("T1", 1, 0, 8, 4, false), new("CT1", 2, 4, 8, 8, true)], false, 1d / 64);
            for (int t = 100; t <= 112; t++) { engine.Now = t; c.Tick(t); }
            Check(engine.Starts.SequenceEqual(new[] { (1, 100), (2, 104) }));
            Check(engine.Handoffs.Count == 0 && engine.Holds.SetEquals([1, 2]) && c.Entries.All(e => e.Phase == VersusPhase.Holding));
            Check(c.Result(false) == null && c.Result(true) == "Timeout");
            c.Cancel(); Check(engine.Stops.SetEquals([1, 2]));
        });
        test("KSV hybrid uses per-role local handoff and keeps other native replays running", () =>
        {
            var engine = new VersusFake(); var c = new VersusCoordinator(engine);
            c.Begin(100, [new("T1", 1, 0, 8, 2, false), new("T3", 3, 4, 8, 8, true), new("CT2", 2, 0, 8, 8, true)], true, 1d / 64);
            for (int t = 100; t <= 102; t++) { engine.Now = t; c.Tick(t); }
            Check(c.Entries[0].Phase == VersusPhase.CombatAI && c.Entries[2].Phase == VersusPhase.Replaying);
            c.MarkDead(1); engine.Now = 103; c.Tick(103); Check(c.Alive(2) == 1 && c.Result(false) == null);
            for (int t = 104; t <= 112; t++) { engine.Now = t; c.Tick(t); }
            Check(engine.Handoffs.Contains((3, 112)) && engine.Handoffs.Contains((2, 108)));
            c.MarkDead(2); engine.Now++; c.Tick(engine.Now); Check(c.Result(false) == "T");
            c.MarkDead(3); engine.Now++; c.Tick(engine.Now); Check(c.Result(false) == "Draw");
        });
        test("KSV rejects duplicate slots and treats disconnected bots as cancellation instead of a kill", () =>
        {
            var engine = new VersusFake(); var c = new VersusCoordinator(engine);
            reject(() => c.Begin(1, [new("T1", 1, 0, 8, 8, true), new("CT1", 1, 0, 8, 8, true)], true, 1d / 64));
            c.Begin(1, [new("T1", 1, 0, 8, 8, true), new("CT1", 2, 0, 8, 8, true)], true, 1d / 64);
            engine.Disconnected.Add(2); reject(() => c.Tick(1)); Check(c.Alive(3) == 1);
        });
        test("Xray palettes are independent, normalized and reject invalid RGB or role injection", () =>
        {
            var ks = XRayPolicy.Merge(false, new Dictionary<string, string> { ["1"] = "aAbBcC" });
            var vs = XRayPolicy.VersusDefaults(); Check(ks["1"] == "#AABBCC" && vs["T1"] == "#FF4141");
            Check(vs.Count == 10 && ks.Count == 5 && vs["CT1"] != vs["T1"]);
            reject(() => XRayPolicy.Normalize("FF0000;quit")); reject(() => XRayPolicy.Normalize("#fff"));
            reject(() => XRayPolicy.Merge(false, new Dictionary<string, string> { ["T1"] = "FF0000" }));
        });
        test("Xray is denied in Defense for every preference and only allowed to authorized spectators during playback", () =>
        {
            Check(XRayPolicy.CanSee(true, true, false, true, true, true));
            for (int bits = 0; bits < 32; bits++)
                Check(!XRayPolicy.CanSee((bits & 1) != 0, (bits & 2) != 0, true, (bits & 4) != 0, (bits & 8) != 0, (bits & 16) != 0));
            Check(!XRayPolicy.CanSee(true, false, false, true, true, true));
            Check(!XRayPolicy.CanSee(true, true, false, false, true, true));
            Check(!XRayPolicy.CanSee(true, true, false, true, false, true));
            Check(!XRayPolicy.CanSee(true, true, false, true, true, false));
        });
        test("Palette persistence merges only its mode and preserves white lists and unknown configuration", () =>
        {
            string file = Path.Combine(temp, "palette-config.json");
            File.WriteAllText(file, "{\"AllowedSteamIDs\":[\"76561198000000001\"],\"DefenseAllowedSteamIDs\":[\"76561198000000002\"],\"VersusAllowedSteamIDs\":[],\"FutureSetting\":{\"nested\":42},\"VersusXRayColors\":{\"T1\":\"#112233\"}}");
            PalettePersistence.Save(file, false, new Dictionary<string, string> { ["1"] = "abcdef" });
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
            Check(node["AllowedSteamIDs"]![0]!.GetValue<string>() == "76561198000000001");
            Check(node["DefenseAllowedSteamIDs"]![0]!.GetValue<string>() == "76561198000000002");
            Check(node["FutureSetting"]!["nested"]!.GetValue<int>() == 42 && node["VersusXRayColors"]!["T1"]!.GetValue<string>() == "#112233");
            Check(node["KsXRayColors"]!["1"]!.GetValue<string>() == "#ABCDEF");
            byte[] before = File.ReadAllBytes(file);
            reject(() => PalettePersistence.Save(file, true, new Dictionary<string, string> { ["T6"] = "FF0000" }));
            Check(before.SequenceEqual(File.ReadAllBytes(file)));
            reject(() => PalettePersistence.Save(Path.Combine(temp, "missing-config.json"), false, XRayPolicy.KsDefaults()));
        });
    }
}

sealed class VersusFake : IVersusReplayEngine
{
    public int Now;
    public List<(int Slot, int Tick)> Starts { get; } = [];
    public List<(int Slot, int Tick)> Handoffs { get; } = [];
    public HashSet<int> Holds { get; } = [];
    public HashSet<int> Stops { get; } = [];
    public HashSet<int> Disconnected { get; } = [];
    public bool Start(int slot) { Starts.Add((slot, Now)); return true; }
    public void Stop(int slot) => Stops.Add(slot);
    public int Cursor(int slot) => Now - Starts.Last(s => s.Slot == slot).Tick;
    public bool IsPlaying(int slot) => Cursor(slot) < 8;
    public bool IsAlive(int slot) => true;
    public bool IsConnected(int slot) => !Disconnected.Contains(slot);
    public bool HandoffToCombatAI(int slot) { Handoffs.Add((slot, Now)); return true; }
    public bool Hold(int slot) { Holds.Add(slot); return true; }
}
