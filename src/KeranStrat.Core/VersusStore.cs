using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotControllerApi;

namespace KeranStrat.Core;

// One atomic archive per tactic: JSON metadata + lossless native struct buffers.
public sealed class VersusStore(string root, int maxVersusScenes = 100, long maxFileBytes = 64 * 1024 * 1024,
    int maxTicks = 11520, long maxStorageBytes = 256 * 1024 * 1024)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    public void EnsureNameAvailable(string map, string name)
    {
        VersusScene.ValidateName(name);
        if (List(map).Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("已存在该战术，无法同名新建。请使用 !ksv load " + name + " 加载后编辑，或换一个名称。");
    }
    private string MapDirectory(string map)
    {
        if (string.IsNullOrWhiteSpace(map) || map.Length > 256) throw new ArgumentException("地图名无效。");
        return Path.Combine(Path.GetFullPath(root), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(map)))[..24]);
    }
    public void Save(VersusScene tactic)
    {
        tactic.Validate(maxTicks);
        lock (_gate)
        {
            string dir = MapDirectory(tactic.Map); Directory.CreateDirectory(dir);
            if (new DirectoryInfo(dir).LinkTarget != null) throw new IOException("不允许符号链接存储目录。");
            string target = Path.Combine(dir, tactic.Id.ToString("N") + ".ksv");
            var existing = List(tactic.Map);
            if (!File.Exists(target) && existing.Count >= maxVersusScenes) throw new IOException("已达到每地图战术数量上限。");
            if (existing.Any(t => t.Id != tactic.Id && t.Name.Equals(tactic.Name, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("同地图已有同名战术，请换一个名称。");
            string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                long expanded = 0;
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    using (var zip = new ZipArchive(file, ZipArchiveMode.Create, true))
                    {
                        // Do not serialize the frame arrays into JSON.
                        var meta = JsonSerializer.Deserialize<VersusScene>(JsonSerializer.Serialize(tactic, MetadataJson))!;
                        using (var output = zip.CreateEntry("versus.json", CompressionLevel.Optimal).Open())
                            JsonSerializer.Serialize(output, meta, Json);
                        foreach (var (role, track) in tactic.Tracks)
                        {
                            using var stream = zip.CreateEntry($"tracks/{role}.bin", CompressionLevel.Fastest).Open();
                            using var writer = new BinaryWriter(stream);
                            writer.Write(0x3156534B); // KSR1
                            expanded += Write(writer, track.Ticks);
                            expanded += Write(writer, track.Subticks);
                            expanded += Write(writer, track.Commands);
                            if (expanded > maxFileBytes) throw new IOException("战术超过展开大小上限。");
                        }
                    }
                    if (file.Length > maxFileBytes) throw new IOException("战术文件过大。");
                    file.Flush(true);
                }
                long stored = Directory.EnumerateFiles(Path.GetFullPath(root), "*.ksv", SearchOption.AllDirectories)
                    .Where(p => p != target).Sum(p => new FileInfo(p).Length);
                if (stored + new FileInfo(temp).Length > maxStorageBytes) throw new IOException("战术总存储配额已满。");
                if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace < 2L * 1024 * 1024 * 1024)
                    throw new IOException("磁盘剩余不足 2 GiB，拒绝新增战术文件。");
                File.Move(temp, target, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    // JSON retains all role metadata but omits the heavy arrays via a resolver.
    private static readonly JsonSerializerOptions MetadataJson = CreateMetadataJson();
    private static JsonSerializerOptions CreateMetadataJson()
    {
        var resolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(Track)) return;
            foreach (var property in info.Properties)
                if (property.Name is nameof(Track.Ticks) or nameof(Track.Subticks) or nameof(Track.Commands))
                    property.ShouldSerialize = (_, _) => false;
        });
        return new JsonSerializerOptions { TypeInfoResolver = resolver };
    }

    private static long Write<T>(BinaryWriter writer, T[] data) where T : unmanaged
    {
        writer.Write(data.Length);
        var bytes = MemoryMarshal.AsBytes(data.AsSpan()); writer.Write(bytes); return bytes.Length;
    }
    private T[] Read<T>(BinaryReader reader, int countLimit, ref long budget) where T : unmanaged
    {
        int count = reader.ReadInt32();
        long bytes = (long)count * Marshal.SizeOf<T>();
        if (count < 0 || count > countLimit || bytes > budget) throw new InvalidDataException("轨迹缓冲长度超限。");
        budget -= bytes;
        var data = new T[count]; reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(data.AsSpan())); return data;
    }

    public IReadOnlyList<VersusScene> List(string map)
    {
        lock (_gate)
        {
            string dir = MapDirectory(map);
            if (!Directory.Exists(dir)) return [];
            var paths = Directory.GetFiles(dir, "*.ksv");
            if (paths.Length > maxVersusScenes) throw new IOException("战术目录超过数量限制。");
            return paths.Select(ReadMetadata).Where(t => t.Map == map).OrderBy(t => t.Name).ToArray();
        }
    }
    private VersusScene ReadMetadata(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget != null || info.Length > maxFileBytes) throw new InvalidDataException("文件过大或为符号链接。");
        using var file = File.OpenRead(path); using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        if (zip.Entries.Count > 11 || zip.Entries.Sum(e => e.Length) > maxFileBytes) throw new InvalidDataException("展开文件超过限制。");
        var entry = zip.GetEntry("versus.json") ?? throw new InvalidDataException("缺少元数据。");
        if (entry.Length > 128 * 1024) throw new InvalidDataException("元数据过大。");
        using var data = entry.Open();
        var tactic = JsonSerializer.Deserialize<VersusScene>(data, Json) ?? throw new InvalidDataException("元数据为空。");
        if (tactic.Kind != "KeranStrat.Versus" || tactic.Format != 1 || tactic.Abi != 22 || tactic.Tracks.Count > 10 || tactic.Id == Guid.Empty)
            throw new InvalidDataException("不支持的元数据版本。");
        if (zip.Entries.Count != tactic.Tracks.Count + 1 || zip.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() != zip.Entries.Count)
            throw new InvalidDataException("KSV 归档目录不完整或包含重复条目。");
        var metadataCheck = tactic.Snapshot(); metadataCheck.Tracks.Clear(); metadataCheck.Handoff.Clear(); metadataCheck.Validate(maxTicks);
        foreach (var (key, track) in tactic.Tracks)
            if (VersusScene.Parse(key).Role != track.Role) throw new InvalidDataException("KSV 角色元数据不匹配。");
        if (Path.GetFileNameWithoutExtension(path) != tactic.Id.ToString("N")) throw new InvalidDataException("战术 ID 与文件不一致。");
        return tactic;
    }
    private VersusScene Resolve(string map, string nameOrId)
    {
        VersusScene.ValidateName(nameOrId);
        return List(map).SingleOrDefault(t => t.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase) ||
            t.Id.ToString().Equals(nameOrId, StringComparison.OrdinalIgnoreCase) || t.Id.ToString("N").Equals(nameOrId, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("当前地图没有这个战术。");
    }
    public VersusScene Load(string map, string nameOrId)
    {
        lock (_gate)
        {
            var tactic = Resolve(map, nameOrId);
            string path = Path.Combine(MapDirectory(map), tactic.Id.ToString("N") + ".ksv");
            using var file = File.OpenRead(path); using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            long budget = maxFileBytes;
            foreach (var (role, track) in tactic.Tracks)
            {
                using var stream = (zip.GetEntry($"tracks/{role}.bin") ?? throw new InvalidDataException("缺少队员轨迹。")).Open();
                using var reader = new BinaryReader(stream);
                if (reader.ReadInt32() != 0x3156534B) throw new InvalidDataException("不支持的轨迹文件头。");
                track.Ticks = Read<ReplayTick>(reader, maxTicks, ref budget);
                track.Subticks = Read<SubtickMove>(reader, maxTicks * 32, ref budget);
                track.Commands = Read<ReplayCommandFrame>(reader, maxTicks, ref budget);
                if (stream.ReadByte() != -1) throw new InvalidDataException("轨迹包含额外数据。");
            }
            tactic.Validate(maxTicks); return tactic;
        }
    }
    public void Delete(string map, string nameOrId)
    {
        lock (_gate)
        {
            var tactic = Resolve(map, nameOrId);
            File.Delete(Path.Combine(MapDirectory(map), tactic.Id.ToString("N") + ".ksv"));
        }
    }
}
