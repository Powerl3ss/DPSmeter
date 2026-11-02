using System;
using System.Collections.Generic;
using System.Linq;

namespace DPSmeter.Core;

public sealed record ActionView(string Name, long Damage, int Hits, float Share);

/// <summary>Une ligne du tableau, prête à être affichée.</summary>
public sealed record CombatantView(
    uint Id,
    string Name,
    uint JobId,
    bool IsLocal,
    long Damage,
    double Dps,
    float Share,
    int Hits,
    float CritRate,
    float DirectHitRate,
    long DotDamage,
    long EstimatedDotDamage,
    IReadOnlyList<ActionView> TopActions);

/// <summary>Photo d'un combat à un instant donné, triée du plus gros DPS au plus petit.</summary>
public sealed record EncounterView(
    string Title,
    string ZoneName,
    bool IsActive,
    DateTime StartUtc,
    double DurationSeconds,
    long TotalDamage,
    double TotalDps,
    IReadOnlyList<CombatantView> Combatants)
{
    private const int MaxActionsShown = 6;

    public CombatantView? Local => Combatants.FirstOrDefault(c => c.IsLocal);

    /// <summary>Plus gros total du combat : les barres sont dessinées par rapport à lui.</summary>
    public long TopDamage => Combatants.Count > 0 ? Combatants[0].Damage : 0;

    public static EncounterView From(Encounter encounter, DateTime nowUtc)
    {
        var duration = encounter.DurationSeconds(nowUtc);
        var total = encounter.TotalDamage;

        var rows = encounter.Combatants
            .OrderByDescending(c => c.Damage)
            .ThenBy(c => c.Id)
            .Select(c => new CombatantView(
                c.Id, c.Name, c.JobId, c.IsLocal, c.Damage,
                c.Damage / duration,
                total > 0 ? (float)(c.Damage / (double)total) : 0f,
                c.Hits,
                c.Hits > 0 ? c.Crits / (float)c.Hits : 0f,
                c.Hits > 0 ? c.DirectHits / (float)c.Hits : 0f,
                c.DotDamage,
                c.EstimatedDotDamage,
                TopActions(c)))
            .ToList();

        return new EncounterView(encounter.Title, encounter.ZoneName, encounter.IsActive, encounter.StartUtc,
                                 duration, total, total / duration, rows);
    }

    private static List<ActionView> TopActions(Combatant combatant) =>
        combatant.Actions
            .OrderByDescending(a => a.Damage)
            .Take(MaxActionsShown)
            .Select(a => new ActionView(a.Name, a.Damage, a.Hits, combatant.Damage > 0 ? (float)(a.Damage / (double)combatant.Damage) : 0f))
            .ToList();

    /// <summary>Faux combat pour régler le HUD sans avoir à se battre.</summary>
    public static EncounterView Sample(string localName, uint localJob)
    {
        (string Name, uint Job, bool Local, long Damage)[] rows =
        {
            (localName, localJob == 0 ? 25u : localJob, true, 4_820_000),
            ("Aria Lumen", 39, false, 4_510_000),
            ("Kael Dorn", 34, false, 4_190_000),
            ("Mira Sol", 38, false, 3_260_000),
            ("Tor Vance", 37, false, 2_480_000),
            ("Lys Arden", 21, false, 2_210_000),
            ("Nea Sorel", 24, false, 1_540_000),
            ("Ivo Marr", 28, false, 1_320_000),
        };

        const double duration = 195;
        var total = rows.Sum(r => r.Damage);
        var views = rows
            .OrderByDescending(r => r.Damage)
            .Select((r, i) => new CombatantView(
                (uint)(i + 1), r.Name, r.Job, r.Local, r.Damage, r.Damage / duration, (float)(r.Damage / (double)total),
                142, 0.24f, 0.31f, (long)(r.Damage * 0.08), 0,
                new List<ActionView>
                {
                    new("Action principale", (long)(r.Damage * 0.42), 58, 0.42f),
                    new("Action secondaire", (long)(r.Damage * 0.27), 31, 0.27f),
                    new("Attaque automatique", (long)(r.Damage * 0.11), 53, 0.11f),
                }))
            .ToList();

        return new EncounterView("Aperçu du HUD", string.Empty, true, DateTime.UtcNow, duration, total, total / duration, views);
    }
}
