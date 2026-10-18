using BotControllerApi;
using KeranStrat.Core;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

int passed = 0;
var temp = Path.Combine(Path.GetTempPath(), "KeranStratTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
void Test(string name, Action action)
{
    try { action(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e); Environment.ExitCode = 1; }
}
void Check(bool condition, string message = "assertion") { if (!condition) throw new Exception(message); }
void Reject(Action work)
{
    bool failed = false; try { work(); } catch { failed = true; }
    Check(failed, "invalid operation was accepted");
}
Tactic Fixture(string name = "B 区战术", string map = "de_mirage")
{
    var tactic = new Tactic { Name = name, Map = map, Creator = "76561197960435530", GameVersion = "fixture" };
    for (int role = 1; role <= 5; role++)
    {
        var ticks = new ReplayTick[64]; var commands = new ReplayCommandFrame[64];
        for (int i = 0; i < ticks.Length; i++)
        {
            ticks[i].Pre.OriginX = i + 0.125f; ticks[i].Post.OriginX = i + 1.125f;
            ticks[i].Pre.Buttons = 1UL << 33; ticks[i].WeaponDefIndex = 7;
            ticks[i].NumSubtick = 1;
            commands[i].Fields = 1 << 8; commands[i].ForwardMove = 200;
        }
        tactic.Tracks[role] = new Track { Role = role, OffsetMs = role == 5 ? 2200 : 0,
            Equipment = [new("weapon_ak47", 17, 55), new("weapon_smokegrenade")],
            Ticks = ticks, Commands = commands,
            Subticks = Enumerable.Range(0, 64).Select(i => new SubtickMove { When = .234f, Button = 1, Pressed = i % 2 }).ToArray() };
    }
    return tactic;
}
bool Bits<T>(T[] a, T[] b) where T : unmanaged => MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));

Test("binary archive preserves five roles, buttons, subticks, commands and equipment", () =>
{
    var store = new FileTacticStore(Path.Combine(temp, "roundtrip")); var original = Fixture(); store.Save(original);
    var loaded = store.Load(original.Map, original.Id.ToString());
    Check(loaded.Creator == original.Creator && loaded.Tracks.Count == 5);
    foreach (int role in original.Tracks.Keys)
    {
        Check(Bits(original.Tracks[role].Ticks, loaded.Tracks[role].Ticks));
        Check(Bits(original.Tracks[role].Subticks, loaded.Tracks[role].Subticks));
        Check(Bits(original.Tracks[role].Commands, loaded.Tracks[role].Commands));
        Check(loaded.Tracks[role].Equipment[0].Clip == 17 && loaded.Tracks[role].OffsetMs == original.Tracks[role].OffsetMs);
    }
});
Test("map isolation, ID/name lookup and deletion", () =>
{
    var store = new FileTacticStore(Path.Combine(temp, "maps")); var original = Fixture(); store.Save(original);
    Reject(() => store.Load("de_dust2", original.Name)); Check(store.List("de_dust2").Count == 0);
    Check(store.Load(original.Map, original.Name).Id == original.Id); store.Delete(original.Map, original.Name);
    Check(store.List(original.Map).Count == 0);
});
Test("path traversal and unsafe names rejected", () =>
{
    var store = new FileTacticStore(Path.Combine(temp, "paths"));
    foreach (string name in new[] { "../outside", "..", "a\\b", "x\n", "" }) Reject(() => store.Save(Fixture(name)));
    Reject(() => store.Load("de_mirage", "../outside"));
});
Test("failed overwrite preserves old archive and does not leave temp files", () =>
{
    string root = Path.Combine(temp, "atomic"); var store = new FileTacticStore(root); var original = Fixture(); store.Save(original);
    original.Tracks[1].Commands = [];
    Reject(() => store.Save(original)); Check(store.Load(original.Map, original.Name).Tracks[1].Commands.Length == 64);
    Check(!Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Any());
});
Test("duplicate names and tactic count limits", () =>
{
    var store = new FileTacticStore(Path.Combine(temp, "counts"), maxTactics: 1); var a = Fixture(); store.Save(a);
    Reject(() => store.Save(Fixture("new"))); store.Save(a);
    var otherStore = new FileTacticStore(Path.Combine(temp, "duplicates")); otherStore.Save(a);
    Reject(() => otherStore.Save(Fixture(a.Name)));
});
Test("ABI, frame count, subtick alignment and recording limits", () =>
{
    var a = Fixture(); a.Abi = 21; Reject(() => a.Validate());
    a = Fixture(); a.CommandSize++; Reject(() => a.Validate());
    a = Fixture(); a.Tracks[1].Subticks = []; Reject(() => a.Validate());
    a = Fixture(); Reject(() => a.Validate(32));
    a = Fixture(); a.Tracks[1].OffsetMs = -1; Reject(() => a.Validate());
    a = Fixture(); a.Tracks[1].Ticks[0].Pre.OriginX = float.NaN; Reject(() => a.Validate());
});
Test("archive byte budget enforced before committing", () =>
{
    var store = new FileTacticStore(Path.Combine(temp, "size"), maxFileBytes: 128);
    Reject(() => store.Save(Fixture())); Check(!Directory.GetFiles(Path.Combine(temp, "size"), "*.ks", SearchOption.AllDirectories).Any());
});
Test("corrupted format metadata rejected", () =>
{
    string root = Path.Combine(temp, "corrupt"); var store = new FileTacticStore(root); var a = Fixture(); store.Save(a);
    string path = Directory.GetFiles(root, "*.ks", SearchOption.AllDirectories).Single();
    using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
    {
        var entry = zip.GetEntry("metadata.json")!; string text;
        using (var reader = new StreamReader(entry.Open())) text = reader.ReadToEnd();
        entry.Delete(); using var writer = new StreamWriter(zip.CreateEntry("metadata.json").Open());
        writer.Write(text.Replace("\"Format\": 1", "\"Format\": 900"));
    }
    Reject(() => store.Load(a.Map, a.Name));
});
Test("all zero-offset roles start on precisely the same scheduling tick", () =>
{
    var engine = new FakeEngine(); var replay = new ReplayCoordinator(engine);
    replay.Begin(100, Enumerable.Range(1, 5).Select(i => new ReplayEntry(i, i, 0, 8)).ToArray());
    engine.Now = 99; replay.Tick(99); Check(engine.Starts.Count == 0);
    engine.Now = 100; replay.Tick(100); Check(engine.Starts.Count == 5 && engine.Starts.All(s => s.Tick == 100));
    for (int t = 101; t <= 108; t++) { engine.Now = t; replay.Tick(t); }
    Check(!replay.Active && replay.Entries.All(e => e.Finished));
});
Test("offsets use ceil to prevent early starts, independent of timer latency", () =>
{
    Check(ReplayCoordinator.ToTicks(1500, 1d / 64) == 96);
    Check(ReplayCoordinator.ToTicks(2200, 1d / 64) == 141);
    Check(ReplayCoordinator.ToTicks(1, 1d / 64) == 1);
    var engine = new FakeEngine(); var replay = new ReplayCoordinator(engine);
    replay.Begin(100, [new(1, 1, 0, 8), new(2, 2, 96, 8)]);
    for (int t = 100; t <= 204; t++) { engine.Now = t; replay.Tick(t); }
    Check(engine.Starts.SequenceEqual(new[] { (1, 100), (2, 196) })); Check(!replay.Active);
});
Test("a failed native start releases every bot", () =>
{
    var engine = new FakeEngine { FailSlot = 2 }; var replay = new ReplayCoordinator(engine);
    replay.Begin(10, [new(1, 1, 0, 8), new(2, 2, 0, 8), new(3, 3, 0, 8)]);
    engine.Now = 10; Reject(() => replay.Tick(10)); Check(!replay.Active && engine.Stops.Contains(3));
});
Test("death, cancellation, repeat replay and slot conflicts", () =>
{
    var engine = new FakeEngine(); var replay = new ReplayCoordinator(engine);
    Reject(() => replay.Begin(1, [new(1, 1, 0, 8), new(2, 1, 0, 8)]));
    replay.Begin(10, [new(1, 1, 0, 8)]); replay.Cancel(); Check(!replay.Active);
    replay.Begin(20, [new(1, 1, 0, 8)]); engine.Dead = true; Reject(() => replay.Tick(20)); Check(!replay.Active);
});
Test("SteamID whitelist denies empty, disabled, unauthenticated and spoofed values", () =>
{
    string id = "76561198000000000";
    var allowed = new HashSet<string> { id, "76561198000000001" };
    Check(AccessPolicy.Allows(true, allowed, id));
    Check(AccessPolicy.Allows(true, allowed, "76561198000000001"));
    Check(!AccessPolicy.Allows(true, new HashSet<string>(), id));
    Check(!AccessPolicy.Allows(false, allowed, id));
    Check(!AccessPolicy.Allows(true, allowed, null));
    Check(!AccessPolicy.Allows(true, allowed, "76561198000000002"));
    Check(!AccessPolicy.IsSteamId64("76561198000000000;quit"));
    Check(!AccessPolicy.IsSteamId64("7656119800000000"));
});
Test("1 to 5 roles and sparse 1,3,5 persist and replay without padding bots", () =>
{
    int[][] groups = [[1], [1, 3], [1, 3, 5], [1, 2, 3, 5], [1, 2, 3, 4, 5]];
    foreach (var roles in groups)
    {
        var tactic = Fixture("group" + roles.Length);
        foreach (int role in tactic.Tracks.Keys.Except(roles).ToArray()) tactic.Tracks.Remove(role);
        var store = new FileTacticStore(Path.Combine(temp, "dynamic")); store.Save(tactic);
        var loaded = store.Load(tactic.Map, tactic.Name); Check(loaded.Tracks.Keys.Order().SequenceEqual(roles));
        var engine = new FakeEngine(); var replay = new ReplayCoordinator(engine);
        replay.Begin(100, roles.Select(r => new ReplayEntry(r, r, 0, 8)).ToArray());
        engine.Now = 100; replay.Tick(100); Check(engine.Starts.Count == roles.Length);
    }
});
Test("removing a role and re-recording a role preserve all other tracks", () =>
{
    var a = Fixture(); var oldThree = a.Tracks[3]; a.Tracks.Remove(2);
    a.Tracks[1] = Fixture().Tracks[1]; Check(ReferenceEquals(a.Tracks[3], oldThree));
    var store = new FileTacticStore(Path.Combine(temp, "remove")); store.Save(a);
    Check(!store.Load(a.Map, a.Name).Tracks.ContainsKey(2));
    var snap = a.Snapshot(); a.Tracks[3].OffsetMs = 1234; Check(snap.Tracks[3].OffsetMs == 0);
});
Test("empty tactics cannot start replay; total storage quota spans maps", () =>
{
    var replay = new ReplayCoordinator(new FakeEngine()); Reject(() => replay.Begin(1, []));
    string root = Path.Combine(temp, "total"); var store = new FileTacticStore(root); var first = Fixture(); store.Save(first);
    long size = Directory.GetFiles(root, "*.ks", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
    var limited = new FileTacticStore(root, maxStorageBytes: size + 1);
    Reject(() => limited.Save(Fixture("another", "de_dust2")));
    Check(limited.Load(first.Map, first.Name).Tracks.Count == 5);
});
Test("AWP and two flashbangs preserve ammo stacks across snapshot and archive", () =>
{
    var tactic = Fixture("AWP 双闪");
    tactic.Tracks[1].Equipment = [new("weapon_awp", 10, 30), new("weapon_flashbang")];
    tactic.Tracks[1].Ammo = new ushort[32];
    tactic.Tracks[1].Ammo[14] = 2; // Synthetic index: verifies lossless storage, not a game-specific enum.
    var store = new FileTacticStore(Path.Combine(temp, "awp-flashes"));
    store.Save(tactic.Snapshot()); var loaded = store.Load(tactic.Map, tactic.Name);
    Check(loaded.Tracks[1].Equipment[0].ClassName == "weapon_awp");
    Check(loaded.Tracks[1].Ammo.SequenceEqual(tactic.Tracks[1].Ammo));
    Check(loaded.Tracks[1].Equipment.Count(e => e.ClassName == "weapon_flashbang") == 1);
    tactic.Tracks[1].Ammo = new ushort[257]; Reject(() => tactic.Validate());
});
Test("old archives without ammo stacks retain backward compatibility", () =>
{
    var tactic = Fixture(); var store = new FileTacticStore(Path.Combine(temp, "old-ammo")); store.Save(tactic);
    Check(store.Load(tactic.Map, tactic.Name).Tracks.Values.All(t => t.Ammo.Length == 0));
});
Test("tail trim aligns ticks, command frames and variable-length subtick streams", () =>
{
    var tactic = Fixture("Trim"); var original = tactic.Tracks[1];
    original.Ammo = [0, 2, 30];
    for (int i = 0; i < original.Ticks.Length; i++) original.Ticks[i].NumSubtick = (uint)(i % 3);
    original.Subticks = Enumerable.Range(0, original.Ticks.Sum(t => (int)t.NumSubtick))
        .Select(i => new SubtickMove { When = i / 1000f }).ToArray();
    var trimmed = TrackEditing.TrimEnd(original, 300, tactic.TickInterval);
    Check(trimmed.Ticks.Length == 44 && trimmed.Commands.Length == 44);
    Check(trimmed.Subticks.Length == original.Ticks.Take(44).Sum(t => (int)t.NumSubtick));
    Check(Bits(trimmed.Subticks, original.Subticks.Take(trimmed.Subticks.Length).ToArray()));
    Check(trimmed.Ammo.SequenceEqual(original.Ammo) && trimmed.OffsetMs == original.OffsetMs);
    Check(original.Ticks.Length == 64 && !ReferenceEquals(trimmed.Ticks, original.Ticks));
    var candidate = tactic.Snapshot(); candidate.Tracks[1] = trimmed; candidate.Validate();
    var store = new FileTacticStore(Path.Combine(temp, "trim")); store.Save(candidate);
    Check(store.Load(candidate.Map, candidate.Name).Tracks[1].Ticks.Length == 44);
    Check(tactic.Tracks[2].Ticks.Length == 64);
});
Test("trim rejects empty results and invalid input without changing original", () =>
{
    var track = Fixture().Tracks[1];
    foreach (int ms in new[] { -1, 0, 1000, 120001 }) Reject(() => TrackEditing.TrimEnd(track, ms, 1d / 64));
    Reject(() => TrackEditing.TrimEnd(track, 1, double.NaN));
    Check(TrackEditing.TrimEnd(track, 984, 1d / 64).Ticks.Length == 1);
    Check(track.Ticks.Length == 64);
    track.Ticks[0].NumSubtick = 100; Reject(() => TrackEditing.TrimEnd(track, 1, 1d / 64));
});
Test("creation preflight rejects shared case-insensitive names without changing archives", () =>
{
    string root = Path.Combine(temp, "name-preflight"); var store = new FileTacticStore(root);
    var existing = Fixture("002"); store.Save(existing);
    var path = Directory.GetFiles(root, "*.ks", SearchOption.AllDirectories).Single(); var bytes = File.ReadAllBytes(path);
    Reject(() => store.EnsureNameAvailable(existing.Map, "002"));
    var letters = Fixture("B_Execute"); store.Save(letters);
    Reject(() => store.EnsureNameAvailable(letters.Map, "b_execute"));
    store.EnsureNameAvailable("de_dust2", "002"); store.EnsureNameAvailable(existing.Map, "003");
    Reject(() => store.EnsureNameAvailable(existing.Map, "../002"));
    Check(bytes.SequenceEqual(File.ReadAllBytes(path)) && store.Load(existing.Map, "002").Id == existing.Id);
    var collaborator = store.Load(existing.Map, "002"); collaborator.Tracks.Remove(5); store.Save(collaborator);
    Check(store.Load(existing.Map, "002").Tracks.Count == 4);
});
DefenseTests.Run(Test, Check, Reject, () => Fixture(), temp);
Test("KS team respawn is delayed, single-use and follows T CT T changes", () =>
{
    var queue = new PracticeTeamRespawn(); var player = new PracticeIdentity(76561197960435530, 2, 10, 99);
    foreach (int team in new[] { 2, 3, 2 })
    {
        queue.Queue(player, team, 100, 16, 320);
        Check(!queue.Take(player, team, false, 115));
        Check(queue.Take(player, team, false, 116));
        Check(!queue.Take(player, team, false, 117));
    }
});
Test("KS respawn rejects slot reuse, stale team, alive pawn and expired request", () =>
{
    var queue = new PracticeTeamRespawn(); var player = new PracticeIdentity(76561197960435530, 2, 10, 99);
    foreach (var other in new[] { player with { SteamId = player.SteamId + 1 }, player with { UserId = 11 }, player with { Handle = 100 } })
    { queue.Queue(player, 3, 100, 16, 320); Check(!queue.Take(other, 3, false, 116)); }
    queue.Queue(player, 3, 100, 16, 320); Check(!queue.Take(player, 2, false, 116));
    queue.Queue(player, 3, 100, 16, 320); Check(!queue.Take(player, 3, true, 116));
    queue.Queue(player, 3, 100, 16, 320); Check(!queue.Take(player, 3, false, 421));
});
Test("KS pending respawn is replaced on rapid changes and cancelled on mode exit", () =>
{
    var queue = new PracticeTeamRespawn(); var player = new PracticeIdentity(76561197960435530, 2, 10, 99);
    queue.Queue(player, 3, 100, 16, 320); queue.Queue(player, 2, 101, 16, 320);
    Check(queue.Take(player, 2, false, 117));
    queue.Queue(player, 3, 100, 16, 320); queue.Queue(player, 1, 101, 16, 320);
    Check(queue.Slots.Length == 0);
    queue.Queue(player, 3, 100, 16, 320); queue.Clear(); Check(!queue.Take(player, 3, false, 116));
});
Test("KS respawn gate excludes Defense including idle lease and map transitions", () =>
{
    Check(PracticeTeamRespawn.Allows(true, true, false, false, false, false));
    Check(!PracticeTeamRespawn.Allows(true, true, false, true, false, false));
    Check(!PracticeTeamRespawn.Allows(true, true, false, false, true, false));
    Check(!PracticeTeamRespawn.Allows(true, true, false, false, false, true));
    Check(!PracticeTeamRespawn.Allows(true, true, true, false, false, false));
    Check(!PracticeTeamRespawn.Allows(false, true, false, false, false, false));
    Check(!PracticeTeamRespawn.Allows(true, false, false, false, false, false));
    var queue = new PracticeTeamRespawn(); var player = new PracticeIdentity(76561197960435530, 2, 10, 99);
    queue.Queue(player, 3, 100, 16, 320);
    Check(!queue.Take(player, 2, true, 101)); // Engine may still expose old team/alive state in event frame.
    Check(queue.Take(player, 3, false, 116));
});
Test("economy variants retain their give alias instead of a shared designer name", () =>
{
    foreach (var (name, definition, alias) in new[] {
        ("weapon_m4a1", 60, "weapon_m4a1_silencer"), ("weapon_hkp2000", 61, "weapon_usp_silencer"),
        ("weapon_p250", 63, "weapon_cz75a"), ("weapon_deagle", 64, "weapon_revolver"),
        ("weapon_mp7", 23, "weapon_mp5sd") })
        Check(WeaponIdentity.ClassName(name, definition) == alias);
    Check(WeaponIdentity.ClassName("weapon_m4a1", 16) == "weapon_m4a1");
    Check(WeaponIdentity.ClassName("weapon_ak47", 7) == "weapon_ak47");
});
Test("legacy M4A1-S equipment is inferred without rewriting native frames or ambiguous guns", () =>
{
    var track = new Track { Equipment = [new("weapon_m4a1", 17, 55), new("weapon_knife")],
        Ticks = [new() { WeaponDefIndex = 60 }, new() { WeaponDefIndex = 42 }] };
    var fixedItems = WeaponIdentity.ForReplay(track);
    Check(fixedItems[0] == new Equipment("weapon_m4a1_silencer", 17, 55));
    Check(fixedItems[1] == track.Equipment[1]);
    Check(track.Equipment[0].ClassName == "weapon_m4a1" && track.Ticks[0].WeaponDefIndex == 60);
    track.Ticks = [new() { WeaponDefIndex = 16 }, new() { WeaponDefIndex = 60 }];
    Check(WeaponIdentity.ForReplay(track)[0].ClassName == "weapon_m4a1");
    track.Ticks = [];
    Check(WeaponIdentity.ForReplay(track)[0].ClassName == "weapon_m4a1");
    track.Equipment = [new("weapon_m4a1_silencer", 20, 80)];
    Check(WeaponIdentity.ForReplay(track)[0] == track.Equipment[0]);
});
VersusTests.Run(Test, Check, Reject, () => Fixture(), temp);
Console.WriteLine($"RESULT: {passed} checks passed; exit={Environment.ExitCode}");
Directory.Delete(temp, true);

sealed class FakeEngine : IReplayEngine
{
    public int Now;
    public int FailSlot = -1;
    public bool Dead;
    public List<(int Slot, int Tick)> Starts { get; } = [];
    public HashSet<int> Stops { get; } = [];
    public bool Start(int slot) { if (slot == FailSlot) return false; Starts.Add((slot, Now)); return true; }
    public void Stop(int slot) => Stops.Add(slot);
    public bool IsPlaying(int slot) => Cursor(slot) < 8;
    public int Cursor(int slot) => Now - Starts.LastOrDefault(s => s.Slot == slot).Tick;
    public bool IsAlive(int slot) => !Dead;
}
