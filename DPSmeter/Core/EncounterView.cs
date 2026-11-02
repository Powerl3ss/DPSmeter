using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace DPSmeter.Core;

/// <summary>Ce que le HUD classe et affiche.</summary>
public enum MeterMetric
{
    Damage,
    Healing,
    DamageTaken,
}

// « Damage » garde son nom dans les fichiers : c'est le champ des combats déjà enregistrés.
public sealed record ActionView(string Name, long Damage, int Hits, float Share, long MaxHit = 0);

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
    // Toutes les actions, de la plus forte à la plus faible. Avant la v0.3, seules les 6 premières étaient gardées.
    [property: JsonPropertyName("TopActions")] IReadOnlyList<ActionView> Actions,
    long Healing = 0,
    long DamageTaken = 0,
    IReadOnlyList<ActionView>? HealActions = null,
    IReadOnlyList<long>? Timeline = null,
    IReadOnlyList<double>? DeathSeconds = null,
    double RecentDps = 0)
{
    [JsonIgnore]
    public int Deaths => DeathSeconds?.Count ?? 0;

    public long ValueOf(MeterMetric metric) => metric switch
    {
        MeterMetric.Healing => Healing,
        MeterMetric.DamageTaken => DamageTaken,
        _ => Damage,
    };
}

/// <summary>Photo d'un combat à un instant donné, triée du plus gros DPS au plus petit.</summary>
public sealed record EncounterView(
    string Title,
    string ZoneName,
    bool IsActive,
    DateTime StartUtc,
    double DurationSeconds,
    long TotalDamage,
    double TotalDps,
    IReadOnlyList<CombatantView> Combatants,
    Outcome Outcome = Outcome.Unknown,
    float RemainingHp = -1f,
    long TotalHealing = 0,
    long TotalDamageTaken = 0)
{
    [JsonIgnore]
    public CombatantView? Local => Combatants.FirstOrDefault(c => c.IsLocal);

    /// <summary>Plus gros total du combat : les barres sont dessinées par rapport à lui.</summary>
    [JsonIgnore]
    public long TopDamage => Combatants.Count > 0 ? Combatants[0].Damage : 0;

    [JsonIgnore]
    public bool HasTimeline => Combatants.Any(c => c.Timeline is { Count: > 0 });

    public long TotalOf(MeterMetric metric) => metric switch
    {
        MeterMetric.Healing => TotalHealing,
        MeterMetric.DamageTaken => TotalDamageTaken,
        _ => TotalDamage,
    };

    /// <summary>Les lignes à afficher pour une mesure : sans celles à zéro, de la plus forte à la plus faible.</summary>
    public List<CombatantView> RowsFor(MeterMetric metric) =>
        Combatants.Where(c => c.ValueOf(metric) > 0)
                  .OrderByDescending(c => c.ValueOf(metric))
                  .ThenBy(c => c.Id)
                  .ToList();

    /// <summary>
    /// DPS d'un point de la courbe. Le dernier point couvre moins de temps que les autres : le diviser
    /// par la largeur normale ferait croire à une chute en fin de combat.
    /// </summary>
    public double TimelineDps(CombatantView row, int index)
    {
        if (row.Timeline == null || index < 0 || index >= row.Timeline.Count)
            return 0;

        var covered = Math.Min(Encounter.TimelineStepSeconds, DurationSeconds - index * (double)Encounter.TimelineStepSeconds);

        // Un point couvrant moins de 2 secondes ne ferait qu'amplifier un seul coup.
        return row.Timeline[index] / Math.Max(2.0, covered);
    }

    public static EncounterView From(Encounter encounter, DateTime nowUtc, int recentWindowSeconds = 10)
    {
        var duration = encounter.DurationSeconds(nowUtc);
        var total = encounter.TotalDamage;
        var points = (int)Math.Ceiling(duration / Encounter.TimelineStepSeconds);

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
                ActionViews(c.Actions, c.Damage),
                c.Healing,
                c.DamageTaken,
                ActionViews(c.HealActions, c.Healing),
                PaddedTimeline(c.Timeline, points),
                c.DeathSeconds.ToList(),
                encounter.IsActive ? c.RecentDps((nowUtc - encounter.StartUtc).TotalSeconds, recentWindowSeconds) : 0))
            .ToList();

        return new EncounterView(encounter.Title, encounter.ZoneName, encounter.IsActive, encounter.StartUtc,
                                 duration, total, total / duration, rows,
                                 encounter.Outcome, encounter.RemainingHp,
                                 encounter.TotalHealing, encounter.TotalDamageTaken);
    }

    /// <summary>Remplace les listes absentes des anciens fichiers par des listes vides.</summary>
    public EncounterView Normalized() => this with
    {
        Combatants = Combatants.Select(c => c with
        {
            Actions = c.Actions ?? Array.Empty<ActionView>(),
            HealActions = c.HealActions ?? Array.Empty<ActionView>(),
            Timeline = c.Timeline ?? Array.Empty<long>(),
            DeathSeconds = c.DeathSeconds ?? Array.Empty<double>(),
        }).ToList(),
    };

    private static List<ActionView> ActionViews(IEnumerable<ActionTotal> actions, long total) =>
        actions.OrderByDescending(a => a.Amount)
               .Select(a => new ActionView(a.Name, a.Amount, a.Hits, total > 0 ? (float)(a.Amount / (double)total) : 0f, a.MaxHit))
               .ToList();

    private static long[] PaddedTimeline(IReadOnlyList<long> timeline, int points)
    {
        var result = new long[Math.Max(points, timeline.Count)];
        for (var i = 0; i < timeline.Count; i++)
            result[i] = timeline[i];
        return result;
    }

    /// <summary>Faux combat pour régler le HUD sans avoir à se battre.</summary>
    public static EncounterView Sample(string localName, uint localJob)
    {
        (string Name, uint Job, bool Local, long Damage, long Healing, long Taken, int Deaths)[] rows =
        {
            (localName, localJob == 0 ? 25u : localJob, true, 4_820_000, 40_000, 310_000, 0),
            ("Aria Lumen", 39, false, 4_510_000, 60_000, 280_000, 1),
            ("Kael Dorn", 34, false, 4_190_000, 30_000, 260_000, 0),
            ("Mira Sol", 38, false, 3_260_000, 120_000, 240_000, 0),
            ("Tor Vance", 37, false, 2_480_000, 310_000, 1_950_000, 0),
            ("Lys Arden", 21, false, 2_210_000, 420_000, 1_620_000, 0),
            ("Nea Sorel", 24, false, 1_540_000, 2_850_000, 220_000, 0),
            ("Ivo Marr", 28, false, 1_320_000, 2_410_000, 230_000, 2),
        };

        const double duration = 195;
        var total = rows.Sum(r => r.Damage);
        var points = (int)Math.Ceiling(duration / Encounter.TimelineStepSeconds);
        var views = rows
            .OrderByDescending(r => r.Damage)
            .Select((r, i) => new CombatantView(
                (uint)(i + 1), r.Name, r.Job, r.Local, r.Damage, r.Damage / duration, (float)(r.Damage / (double)total),
                142, 0.24f, 0.31f, (long)(r.Damage * 0.08), 0,
                new List<ActionView>
                {
                    new("Action principale", (long)(r.Damage * 0.42), 58, 0.42f, (long)(r.Damage * 0.02)),
                    new("Action secondaire", (long)(r.Damage * 0.27), 31, 0.27f, (long)(r.Damage * 0.015)),
                    new("Attaque automatique", (long)(r.Damage * 0.11), 53, 0.11f, (long)(r.Damage * 0.004)),
                },
                r.Healing,
                r.Taken,
                new List<ActionView>(),
                Enumerable.Range(0, points).Select(p => (long)(r.Damage / (double)points * (1 + 0.5 * Math.Sin(p * 0.5 + i)))).ToList(),
                Enumerable.Range(0, r.Deaths).Select(d => 60.0 + d * 45).ToList(),
                r.Damage / duration * 1.15))
            .ToList();

        return new EncounterView("Aperçu du HUD", string.Empty, true, DateTime.UtcNow, duration, total, total / duration, views,
                                 Outcome.Unknown, -1f, rows.Sum(r => r.Healing), rows.Sum(r => r.Taken));
    }
}
