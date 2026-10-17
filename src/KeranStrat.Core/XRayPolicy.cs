using System.Text.RegularExpressions;

namespace KeranStrat.Core;

public static class XRayPolicy
{
    public static Dictionary<string, string> KsDefaults() => new()
    { ["1"] = "#FF4141", ["2"] = "#FF8B27", ["3"] = "#FFC83D", ["4"] = "#35D88B", ["5"] = "#2588FF" };
    public static Dictionary<string, string> VersusDefaults() => new()
    {
        ["T1"] = "#FF4141", ["T2"] = "#FF8B27", ["T3"] = "#FFC83D", ["T4"] = "#FF50AA", ["T5"] = "#CC55FF",
        ["CT1"] = "#2588FF", ["CT2"] = "#22D1E8", ["CT3"] = "#35D88B", ["CT4"] = "#A7EE49", ["CT5"] = "#B0BCDA"
    };
    public static string Normalize(string color)
    {
        if (!Regex.IsMatch(color, "^#?[0-9a-fA-F]{6}$")) throw new ArgumentException("颜色必须为六位十六进制 RGB，例如 FF0000 或 #FF0000。");
        return "#" + color.TrimStart('#').ToUpperInvariant();
    }
    public static Dictionary<string, string> Merge(bool versus, IReadOnlyDictionary<string, string> colors)
    {
        var result = versus ? VersusDefaults() : KsDefaults();
        foreach (var (key, color) in colors)
        {
            if (!result.ContainsKey(key)) throw new ArgumentException("颜色表含不存在的角色。");
            result[key] = Normalize(color);
        }
        return result;
    }
    public static bool CanSee(bool practice, bool playing, bool defense, bool authorized, bool spectator, bool enabled) =>
        practice && playing && !defense && authorized && spectator && enabled;
}
