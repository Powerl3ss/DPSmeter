using System;
using System.Collections.Generic;
using System.Linq;
using DPSmeter.Core;

// Petit lanceur sans dépendance : chaque test est une méthode, un échec fait échouer la CI.
var tests = new (string Name, Action Run)[]
{
    ("Un coup direct démarre le combat", Tests.DirectHitStartsEncounter),
    ("Un DoT seul ne démarre pas de combat", Tests.DotDoesNotStartEncounter),
    ("Un DoT compte pendant un combat", Tests.DotCountsDuringEncounter),
    ("Le DPS vaut le total divisé par la durée", Tests.DpsIsTotalOverDuration),
    ("La durée minimale évite une division absurde", Tests.ShortFightUsesOneSecondMinimum),
    ("Pas de fin de combat tant que le jeu dit en combat", Tests.StaysOpenWhileInCombat),
    ("Fin de combat après le délai, durée au dernier coup", Tests.EndsAfterDelayAndFreezesDuration),
    ("Un coup récent retarde la fin de combat", Tests.RecentHitDelaysEnd),
    ("Le combat terminé reste affiché, le suivant le remplace", Tests.DisplayedFollowsEncounters),
    ("L'historique respecte sa limite", Tests.HistoryIsCapped),
    ("Le titre vient de la cible la plus touchée", Tests.TitleIsMostDamagedTarget),
    ("Le titre retombe sur la zone", Tests.TitleFallsBackToZone),
    ("Le tableau est trié et les parts font 100 %", Tests.ViewIsSortedAndSharesSumToOne),
    ("Taux de critiques calculés sur les coups directs", Tests.RatesIgnoreDots),
    ("Dégâts de DoT estimés comptés à part", Tests.EstimatedDotsAreTracked),
    ("Partage de DoT : la somme vaut le tick", Tests.SplitKeepsTotal),
    ("Partage de DoT : proportionnel à la puissance", Tests.SplitFollowsWeights),
    ("Partage de DoT : parts égales sans poids", Tests.SplitEqualWithoutWeights),
    ("Partage de DoT : petit tick, beaucoup de joueurs", Tests.SplitSmallAmount),
    ("Table des DoT : effets au sol reconnus", Tests.DotTableKnowsGround),
    ("Format court des nombres", Tests.CompactFormat),
    ("Format complet des nombres", Tests.FullFormat),
    ("Format des durées", Tests.DurationFormat),
    ("Aucun tiret long dans les textes affichés", Tests.TooltipHasNoLongDash),
};

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"ok     {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"ÉCHEC  {name} : {ex.Message}");
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests réussis");
return failed == 0 ? 0 : 1;

internal static class Tests
{
    private static readonly DateTime T0 = new(2026, 11, 2, 20, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double seconds) => T0.AddSeconds(seconds);

    private static Hit Direct(uint id, long amount, string target = "Boss", bool crit = false, bool direct = false, bool local = false) =>
        new(id, $"Joueur{id}", 25, local, target, "Feu", amount, crit, direct);

    private static Hit Dot(uint id, long amount, bool estimated = false) =>
        new(id, $"Joueur{id}", 25, false, "Boss", "Foudre (DoT)", amount, IsDot: true, IsEstimated: estimated);

    private static void Equal<T>(T expected, T actual, string what = "valeur")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what} : attendu {expected}, obtenu {actual}");
    }

    private static void Near(double expected, double actual, double tolerance = 0.01)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new Exception($"attendu {expected}, obtenu {actual}");
    }

    private static void True(bool condition, string what)
    {
        if (!condition)
            throw new Exception(what);
    }

    public static void DirectHitStartsEncounter()
    {
        var tracker = new EncounterTracker();
        True(tracker.Record(Direct(1, 100), T0, "Zone"), "coup refusé");
        True(tracker.IsActive, "combat non démarré");
    }

    public static void DotDoesNotStartEncounter()
    {
        var tracker = new EncounterTracker();
        True(!tracker.Record(Dot(1, 100), T0, "Zone"), "tick accepté hors combat");
        True(!tracker.IsActive, "faux combat ouvert");
        True(tracker.Displayed == null, "rien ne devrait être affiché");
    }

    public static void DotCountsDuringEncounter()
    {
        var tracker = new EncounterTracker();
        tracker.Record(Direct(1, 100), T0, "Zone");
        True(tracker.Record(Dot(1, 50), At(3), "Zone"), "tick refusé en combat");
        Equal(150L, tracker.Displayed!.TotalDamage, "total");
    }

    public static void DpsIsTotalOverDuration()
    {
        var tracker = new EncounterTracker();
        tracker.Record(Direct(1, 1000), T0, "Zone");
        tracker.Record(Direct(1, 1000), At(10), "Zone");

        var view = tracker.BuildView(tracker.Displayed, At(20))!;
        Near(20, view.DurationSeconds);
        Near(100, view.Combatants[0].Dps);
        Near(100, view.TotalDps);
    }

    public static void ShortFightUsesOneSecondMinimum()
    {
        var tracker = new EncounterTracker();
        tracker.Record(Direct(1, 500), T0, "Zone");
        var view = tracker.BuildView(tracker.Displayed, T0)!;
        Near(1, view.DurationSeconds);
        Near(500, view.Combatants[0].Dps);
    }

    public static void StaysOpenWhileInCombat()
    {
        var tracker = new EncounterTracker();
        tracker.Record(Direct(1, 100), T0, "Zone");
        tracker.Update(At(300), inCombat: true, endDelaySeconds: 5);
        True(tracker.IsActive, "le boss inciblable ne doit pas clore le combat");
    }

    public static void EndsAfterDelayAndFreezesDuration()
    {
        var tracker = new EncounterTracker();
        Encounter? ended = null;
        tracker.EncounterEnded += e => ended = e;
        tracker.Record(Direct(1, 1000), T0, "Zone");
        tracker.Record(Direct(1, 1000), At(10), "Zone");

        tracker.Update(At(12), inCombat: false, endDelaySeconds: 5);
        True(tracker.IsActive, "fini trop tôt");

        tracker.Update(At(16), inCombat: false, endDelaySeconds: 5);
        True(!tracker.IsActive, "pas terminé après le délai");
        True(ended != null, "événement de fin manquant");

        // La durée ne doit plus bouger, et ne compte pas le délai d'attente.
        var view = tracker.BuildView(tracker.Displayed, At(500))!;
        Near(10, view.DurationSeconds);
        Near(200, view.TotalDps);
        True(!view.IsActive, "devrait être terminé");
    }

    public static void RecentHitDelaysEnd()
    {
        var tracker = new EncounterTracker();
        tracker.Record(Direct(1, 100), T0, "Zone");
        tracker.Record(Direct(2, 100), At(9), "Zone"); // toi mort, ton groupe se bat encore
        tracker.Update(At(10), inCombat: false, endDelaySeconds: 5);
        True(tracker.IsActive, "le groupe frappe encore");
    }

    public static void DisplayedFollowsEncounters()
    {
        var tracker = new EncounterTracker();
        tracker.Record(Direct(1, 100, "Premier"), T0, "Zone");
        tracker.EndCurrent(At(1));
        Equal("Premier", tracker.Displayed!.Title);

        tracker.Record(Direct(1, 100, "Second"), At(30), "Zone");
        Equal("Second", tracker.Displayed!.Title);
        Equal(1, tracker.History.Length, "historique");
    }

    public static void HistoryIsCapped()
    {
        var tracker = new EncounterTracker { MaxHistory = 3 };
        for (var i = 0; i < 5; i++)
        {
            tracker.Record(Direct(1, 100, $"Boss{i}"), At(i * 10), "Zone");
            tracker.EndCurrent(At(i * 10 + 1));
        }

        Equal(3, tracker.History.Length, "historique");
        Equal("Boss4", tracker.History[0].Title);
        Equal("Boss2", tracker.History[2].Title);
    }

    public static void TitleIsMostDamagedTarget()
    {
        var encounter = new Encounter(T0, "Zone");
        encounter.Add(Direct(1, 100, "Sbire"), At(1));
        encounter.Add(Direct(1, 900, "Boss"), At(2));
        encounter.Add(Direct(1, 200, "Sbire"), At(3));
        Equal("Boss", encounter.Title);
    }

    public static void TitleFallsBackToZone()
    {
        var encounter = new Encounter(T0, "Limsa");
        encounter.Add(Direct(1, 100, string.Empty), At(1));
        Equal("Limsa", encounter.Title);
        Equal("Combat", new Encounter(T0, string.Empty).Title);
    }

    public static void ViewIsSortedAndSharesSumToOne()
    {
        var tracker = new EncounterTracker();
        tracker.Record(Direct(1, 100), T0, "Zone");
        tracker.Record(Direct(2, 700), T0, "Zone");
        tracker.Record(Direct(3, 200, local: true), T0, "Zone");

        var view = tracker.BuildView(tracker.Displayed, At(10))!;
        Equal(2u, view.Combatants[0].Id, "premier");
        Equal(3u, view.Combatants[1].Id, "deuxième");
        Equal(1u, view.Combatants[2].Id, "troisième");
        Near(1, view.Combatants.Sum(c => c.Share));
        Equal(3u, view.Local!.Id, "ligne du joueur");
        Equal(700L, view.TopDamage, "meilleur total");
    }

    public static void RatesIgnoreDots()
    {
        var tracker = new EncounterTracker();
        tracker.Record(Direct(1, 100, crit: true, direct: true), T0, "Zone");
        tracker.Record(Direct(1, 100), At(1), "Zone");
        tracker.Record(Direct(1, 100, crit: true), At(2), "Zone");
        tracker.Record(Direct(1, 100), At(3), "Zone");
        tracker.Record(Dot(1, 100), At(4), "Zone");

        var row = tracker.BuildView(tracker.Displayed, At(5))!.Combatants[0];
        Equal(4, row.Hits, "coups");
        Near(0.5, row.CritRate);
        Near(0.25, row.DirectHitRate);
        Equal(100L, row.DotDamage, "DoT");
    }

    public static void EstimatedDotsAreTracked()
    {
        var tracker = new EncounterTracker();
        tracker.Record(Direct(1, 100), T0, "Zone");
        tracker.Record(Dot(1, 40, estimated: true), At(1), "Zone");
        tracker.Record(Dot(1, 60), At(2), "Zone");

        var row = tracker.BuildView(tracker.Displayed, At(3))!.Combatants[0];
        Equal(100L, row.DotDamage, "DoT");
        Equal(40L, row.EstimatedDotDamage, "DoT estimés");
        True(Formatting.Tooltip(row, "Joueur1", "MNO").Contains("estimés"), "l'infobulle doit le signaler");
    }

    public static void SplitKeepsTotal()
    {
        var candidates = new[] { new DotShare(1, 1871, 85), new DotShare(2, 1895, 85), new DotShare(3, 1201, 25) };
        foreach (var amount in new uint[] { 1, 2, 7, 1000, 12345, 999_999 })
        {
            var parts = DotTable.Split(amount, candidates);
            Equal(3, parts.Count, "parts");
            Equal((long)amount, parts.Sum(p => (long)p.Amount), $"somme pour {amount}");
        }
    }

    public static void SplitFollowsWeights()
    {
        var parts = DotTable.Split(1000, new[] { new DotShare(1, 0, 75), new DotShare(2, 0, 25) });
        Equal(750u, parts[0].Amount, "grosse part");
        Equal(250u, parts[1].Amount, "petite part");
        Equal(0, DotTable.Split(100, Array.Empty<DotShare>()).Count, "aucun candidat");
        Equal(0, DotTable.Split(0, new[] { new DotShare(1, 0, 1) }).Count, "tick vide");
    }

    public static void SplitEqualWithoutWeights()
    {
        var parts = DotTable.Split(90, new[] { new DotShare(1, 0, 0), new DotShare(2, 0, 0), new DotShare(3, 0, 0) });
        True(parts.All(p => p.Amount == 30), "parts inégales");
    }

    public static void SplitSmallAmount()
    {
        var candidates = Enumerable.Range(1, 8).Select(i => new DotShare((uint)i, 0, 1)).ToArray();
        var parts = DotTable.Split(3, candidates);
        Equal(3L, parts.Sum(p => (long)p.Amount), "somme");
        True(parts.All(p => p.Amount <= 1), "un joueur reçoit trop");
    }

    public static void DotTableKnowsGround()
    {
        True(DotTable.IsGround(501), "Doton");
        True(DotTable.IsKnownDot(501), "Doton connu");
        True(!DotTable.IsGround(1871), "Dia n'est pas au sol");
        True(DotTable.IsKnownDot(1871), "Dia connu");
        Near(85, DotTable.WeightOf(1871));
        Near(1, DotTable.WeightOf(999999));
    }

    public static void CompactFormat()
    {
        Equal("0", Formatting.Compact(0));
        Equal("987", Formatting.Compact(987.4));
        Equal("1,0k", Formatting.Compact(1000));
        Equal("12,3k", Formatting.Compact(12_345));
        Equal("99,9k", Formatting.Compact(99_999));
        Equal("123k", Formatting.Compact(123_456));
        Equal("1,23M", Formatting.Compact(1_234_567));
        Equal("0", Formatting.Compact(double.NaN));
    }

    public static void FullFormat()
    {
        Equal("0", Formatting.Full(0));
        Equal("999", Formatting.Full(999));
        Equal("1 000", Formatting.Full(1000));
        Equal("1 234 567", Formatting.Full(1_234_567));
    }

    public static void DurationFormat()
    {
        Equal("0:05", Formatting.Duration(5.9));
        Equal("3:21", Formatting.Duration(201));
        Equal("1:01:05", Formatting.Duration(3665));
    }

    public static void TooltipHasNoLongDash()
    {
        var row = EncounterView.Sample("Moi", 25).Combatants[0];
        True(!Formatting.Tooltip(row, row.Name, "MNO").Contains('-'), "tiret long dans l'infobulle");
    }
}
