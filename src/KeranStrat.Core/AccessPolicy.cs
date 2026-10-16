namespace KeranStrat.Core;

public static class AccessPolicy
{
    public static bool IsSteamId64(string? value) => value is { Length: 17 } && value.All(char.IsAsciiDigit) &&
        ulong.TryParse(value, out ulong id) && id >= 76561197960265728UL && id <= 76561202255233023UL;

    public static bool Allows(bool enabled, IReadOnlySet<string> allowed, string? authenticatedId) =>
        enabled && IsSteamId64(authenticatedId) && allowed.Contains(authenticatedId!);
}
