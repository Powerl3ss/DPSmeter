using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;

namespace DPSmeter.Core;

/// <summary>Mise en forme des nombres, durées et infobulles.</summary>
public static class Formatting
{
    /// <summary>Nombre court pour le HUD : 987, 12,3k, 123k, 1,23M.</summary>
    public static string Compact(double value)
    {
        if (double.IsNaN(value) || value < 0)
            value = 0;

        string text;
        if (value < 1_000)
            text = value.ToString("0", CultureInfo.InvariantCulture);
        else if (value < 100_000)
            text = (Math.Floor(value / 100) / 10).ToString("0.0", CultureInfo.InvariantCulture) + "k";
        else if (value < 1_000_000)
            text = Math.Floor(value / 1_000).ToString("0", CultureInfo.InvariantCulture) + "k";
        else
            text = (Math.Floor(value / 10_000) / 100).ToString("0.00", CultureInfo.InvariantCulture) + "M";

        return text.Replace('.', ',');
    }

    /// <summary>Nombre complet avec séparateur des milliers : 1 234 567.</summary>
    public static string Full(double value)
    {
        var digits = Math.Round(Math.Max(0, value)).ToString("0", CultureInfo.InvariantCulture);
        var result = new StringBuilder(digits.Length + digits.Length / 3);
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0)
                result.Append(' ');
            result.Append(digits[i]);
        }
        return result.ToString();
    }

    public static string Percent(float fraction) => $"{MathF.Round(fraction * 100f):0} %";

    public static string Duration(double seconds)
    {
        var total = (long)Math.Max(0, seconds);
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{secs:00}" : $"{minutes}:{secs:00}";
    }

    public static string OutcomeLabel(Outcome outcome) => outcome switch
    {
        Outcome.Victory => "Victoire",
        Outcome.Failure => "Échec",
        Outcome.Interrupted => "Interrompu",
        _ => string.Empty,
    };

    /// <summary>Étiquette d'un combat : « Échec | boss à 12 % » si les PV restants sont connus.</summary>
    public static string OutcomeText(Outcome outcome, float remainingHp)
    {
        var label = OutcomeLabel(outcome);
        return outcome == Outcome.Failure && remainingHp >= 0f ? $"{label}  |  cible à {OneDecimalPercent(remainingHp)}" : label;
    }

    public static string MetricLabel(MeterMetric metric) => metric switch
    {
        MeterMetric.Healing => "Soins",
        MeterMetric.DamageTaken => "Dégâts subis",
        _ => "Dégâts",
    };

    public static string OneDecimalPercent(float fraction) =>
        (fraction * 100f).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " %";

    /// <summary>Infobulle d'un combattant : tout ce qui n'a pas sa place sur la barre.</summary>
    public static string Tooltip(CombatantView row, string shownName, string job, double durationSeconds, int recentWindowSeconds)
    {
        const int MaxActions = 6;

        var lines = new List<string>
        {
            job.Length > 0 ? $"{shownName} ({job})" : shownName,
            $"{Full(row.Dps)} DPS  |  {Percent(row.Share)} des dégâts",
        };

        if (row.RecentDps > 0)
            lines.Add($"{Full(row.RecentDps)} DPS sur les {recentWindowSeconds} dernières secondes");

        lines.Add($"{Full(row.Damage)} dégâts");

        if (row.Hits > 0)
            lines.Add($"{row.Hits} coups  |  {Percent(row.CritRate)} critiques  |  {Percent(row.DirectHitRate)} directs");

        if (row.DotDamage > 0)
        {
            var estimated = row.EstimatedDotDamage > 0 ? $" (dont {Full(row.EstimatedDotDamage)} estimés)" : string.Empty;
            lines.Add($"DoT : {Full(row.DotDamage)}{estimated}");
        }

        if (row.Healing > 0)
            lines.Add($"Soins, surplus compris : {Full(row.Healing)}  ({Compact(row.Healing / Math.Max(1.0, durationSeconds))}/s)");
        if (row.DamageTaken > 0)
            lines.Add($"Dégâts subis : {Full(row.DamageTaken)}");
        if (row.Deaths > 0)
            lines.Add(row.Deaths > 1 ? $"{row.Deaths} morts" : "1 mort");

        if (row.Actions is { Count: > 0 })
        {
            lines.Add(string.Empty);
            foreach (var action in row.Actions.Take(MaxActions))
                lines.Add($"{action.Name} : {Compact(action.Damage)}  ({Percent(action.Share)}, {action.Hits}x)");
            if (row.Actions.Count > MaxActions)
                lines.Add($"... et {row.Actions.Count - MaxActions} autre(s)");
        }

        return string.Join("\n", lines);
    }
}

/// <summary>Couleur de barre de chaque job (identifiants de la feuille ClassJob du jeu).</summary>
public static class JobColors
{
    /// <summary>Ligne de la Transcendance (Limit Break).</summary>
    public const uint LimitBreakId = 0;

    private static readonly Dictionary<uint, Vector4> Colors = new()
    {
        [1] = Rgb(0xA8D2E6), [19] = Rgb(0xA8D2E6), // Gladiateur, Paladin
        [3] = Rgb(0xCF2621), [21] = Rgb(0xCF2621), // Maraudeur, Guerrier
        [32] = Rgb(0xD126CC),                      // Chevalier noir
        [37] = Rgb(0x9C8C4C),                      // Pistosabreur
        [6] = Rgb(0xFFF0DC), [24] = Rgb(0xFFF0DC), // Élémentaliste, Mage blanc
        [28] = Rgb(0x8657FF),                      // Érudit
        [33] = Rgb(0xFFE74A),                      // Astromancien
        [40] = Rgb(0x80A0F0),                      // Sage
        [2] = Rgb(0xD69C00), [20] = Rgb(0xD69C00), // Pugiliste, Moine
        [4] = Rgb(0x4164CD), [22] = Rgb(0x4164CD), // Maître d'hast, Chevalier dragon
        [29] = Rgb(0xAF1964), [30] = Rgb(0xAF1964), // Surineur, Ninja
        [34] = Rgb(0xE46D04),                      // Samouraï
        [39] = Rgb(0x965A90),                      // Faucheur
        [41] = Rgb(0x2E9E3C),                      // Rôdeur vipère
        [5] = Rgb(0x91BA5E), [23] = Rgb(0x91BA5E), // Archer, Barde
        [31] = Rgb(0x6EE1D6),                      // Machiniste
        [38] = Rgb(0xE2B0AF),                      // Danseur
        [7] = Rgb(0xA579D6), [25] = Rgb(0xA579D6), // Occultiste, Mage noir
        [26] = Rgb(0x2D9B78), [27] = Rgb(0x2D9B78), // Arcaniste, Invocateur
        [35] = Rgb(0xE87B7B),                      // Mage rouge
        [36] = Rgb(0x2459FF),                      // Mage bleu
        [42] = Rgb(0xFCA9D9),                      // Pictomancien
    };

    public static Vector4? Of(uint jobId) => Colors.TryGetValue(jobId, out var color) ? color : null;

    private static Vector4 Rgb(int rgb) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
}
