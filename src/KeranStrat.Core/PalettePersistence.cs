using System.Text.Json;
using System.Text.Json.Nodes;

namespace KeranStrat.Core;

public static class PalettePersistence
{
    public static void Save(string path, bool versus, IReadOnlyDictionary<string, string> palette)
    {
        var colors = XRayPolicy.Merge(versus, palette);
        byte[] before = File.ReadAllBytes(path);
        if (before.Length > 1024 * 1024) throw new IOException("配置文件过大。");
        var node = JsonNode.Parse(before, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject()
            ?? throw new InvalidDataException("配置文件无效。");
        string property = versus ? "VersusXRayColors" : "KsXRayColors";
        foreach (string oldKey in node.Select(kv => kv.Key).Where(k => k.Equals(property, StringComparison.OrdinalIgnoreCase)).ToArray()) node.Remove(oldKey);
        node[property] = JsonSerializer.SerializeToNode(colors);
        string temp = path + ".xray-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(f, node, new JsonSerializerOptions { WriteIndented = true }); f.Flush(true); }
            if (!before.SequenceEqual(File.ReadAllBytes(path))) throw new IOException("配置被外部修改，请重试，未覆盖其他改动。");
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
