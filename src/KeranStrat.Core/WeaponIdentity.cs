namespace KeranStrat.Core;

// Several economy weapons share their entity designer name with another gun.
// Store the GiveNamedItem alias in the existing ClassName field, not that name.
public static class WeaponIdentity
{
    private static readonly (string Base, int BaseDef, int VariantDef, string Alias)[] Variants =
    [
        ("weapon_m4a1", 16, 60, "weapon_m4a1_silencer"),
        ("weapon_hkp2000", 32, 61, "weapon_usp_silencer"),
        ("weapon_p250", 36, 63, "weapon_cz75a"),
        ("weapon_deagle", 1, 64, "weapon_revolver"),
        ("weapon_mp7", 33, 23, "weapon_mp5sd")
    ];

    public static string ClassName(string designerName, int definition)
    {
        foreach (var v in Variants)
            if (designerName == v.Base && definition == v.VariantDef) return v.Alias;
        return designerName;
    }

    public static Equipment[] ForReplay(Track track)
    {
        var definitions = track.Ticks.Select(t => t.WeaponDefIndex).ToHashSet();
        return track.Equipment.Select(item =>
        {
            foreach (var v in Variants)
                // Old archives have no economy definition in their equipment metadata.
                // Infer only an unambiguous variant actually present in native frames.
                if (item.ClassName == v.Base && definitions.Contains(v.VariantDef) && !definitions.Contains(v.BaseDef) &&
                    !track.Equipment.Any(e => e.ClassName == v.Alias))
                    return item with { ClassName = v.Alias };
            return item;
        }).ToArray();
    }
}
