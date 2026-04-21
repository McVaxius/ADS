using System.Numerics;

namespace ADS.Models;

public sealed record DutyCategoryDisplayEntry(
    DutyCategory Category,
    string FilterLabel,
    string ReadinessLabel,
    string MaturityLabel,
    Vector4 Accent);

public static class DutyCategoryDisplayCatalog
{
    public static readonly IReadOnlyList<DutyCategoryDisplayEntry> Entries =
    [
        new(DutyCategory.Solo, "Solo", "Solo runtime lane", "Solo maturity", new Vector4(0.64f, 0.88f, 1.0f, 1f)),
        new(DutyCategory.FourMan, "4-Man", "4-man runtime lane", "4-man maturity", new Vector4(0.42f, 0.94f, 0.64f, 1f)),
        new(DutyCategory.EightMan, "8-Man", "8-man runtime lane", "8-man maturity", new Vector4(0.92f, 0.80f, 0.36f, 1f)),
        new(DutyCategory.AllianceRaid, "Alliance", "Alliance runtime lane", "Alliance maturity", new Vector4(0.96f, 0.58f, 0.36f, 1f)),
        new(DutyCategory.GuildHest, "Guild Hest", "Guild Hest runtime lane", "Guild Hest maturity", new Vector4(0.90f, 0.72f, 1.0f, 1f)),
        new(DutyCategory.DeepDungeon, "Deep Dungeon", "Deep Dungeon runtime lane", "Deep Dungeon maturity", new Vector4(0.56f, 0.86f, 0.86f, 1f)),
        new(DutyCategory.TreasureDungeon, "Treasure", "Treasure runtime lane", "Treasure maturity", new Vector4(1.0f, 0.88f, 0.46f, 1f)),
        new(DutyCategory.Other, "Other", "Other runtime lane", "Other maturity", new Vector4(0.78f, 0.78f, 0.78f, 1f)),
    ];

    public static DutyCategoryDisplayEntry Get(DutyCategory category)
        => Entries.FirstOrDefault(x => x.Category == category) ?? Entries[^1];
}
