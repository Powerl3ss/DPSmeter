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
    ("Sauvegarde : un combat relu est identique", Tests.StoreRoundTrip),
    ("Sauvegarde : les accents survivent au rechargement", Tests.StoreKeepsAccents),
    ("Sauvegarde : un fichier abîmé n'empêche pas de lire les autres", Tests.StoreSkipsCorruptFile),
    ("Sauvegarde : un combat supprimé ne revient pas", Tests.StoreDeleteIsPermanent),
    ("Sauvegarde : aucun fichier temporaire ne reste", Tests.StoreLeavesNoTemporaryFile),
    ("Sauvegarde : un combat ajouté pendant le chargement est gardé", Tests.StoreKeepsRecordAddedBeforeLoad),
    ("Historique : filtres par type, lieu et personnage", Tests.QueryFilters),
    ("Historique : tris", Tests.QuerySorts),
    ("Historique : options de filtre avec leur nombre", Tests.QueryOptions),
    ("Ancien fichier (v1) : relu sans perte et sans erreur", Tests.StoreReadsVersionOneFile),
    ("Sauvegarde : toutes les actions et la courbe sont conservées", Tests.StoreKeepsFullDetail),
    ("Actions : toutes gardées, avec le plus gros coup", Tests.AllActionsAreKept),
    ("Courbe : dégâts rangés par tranche de 5 secondes", Tests.TimelineBuckets),
    ("Courbe : le dernier point partiel ne chute pas", Tests.TimelineLastPointIsScaled),
    ("DPS récent : ne compte que la fenêtre choisie", Tests.RecentDpsUsesWindow),
    ("DPS récent : en début de combat, divise par le temps écoulé", Tests.RecentDpsAtStart),
    ("DPS récent : retombe à zéro quand on arrête de frapper", Tests.RecentDpsDecays),
    ("DPS récent : à zéro une fois le combat terminé", Tests.RecentDpsZeroWhenEnded),
    ("Issue : cible morte, victoire", Tests.OutcomeVictoryOnDeath),
    ("Issue : cible à 0 PV, victoire", Tests.OutcomeVictoryOnZeroHp),
    ("Issue : cible vivante, échec avec PV restants", Tests.OutcomeFailureKeepsHp),
    ("Issue : cible intacte (mannequin), interrompu", Tests.OutcomeInterruptedOnDummy),
    ("Issue : sans information, inconnue", Tests.OutcomeUnknownWithoutHp),
    ("Issue : l'annonce du jeu prime", Tests.OutcomeMarkWins),
    ("Issue : c'est la cible la plus touchée qui compte", Tests.OutcomeUsesMainTarget),
    ("Issue : une annonce juste après la fin corrige le combat", Tests.OutcomeLateMark),
    ("Soins, subis, morts : comptés pendant un combat", Tests.SupportStatsAreCounted),
    ("Soins, subis, morts : ne démarrent jamais de combat", Tests.SupportStatsDoNotStart),
    ("Mesures : classement et lignes à zéro retirées", Tests.RowsForMetric),
    ("Filtres : par job et par issue", Tests.QueryFiltersJobAndOutcome),
    ("Records : le meilleur par adversaire, job et personnage", Tests.PersonalBestsCompute),
    ("Records : échecs et combats trop courts exclus", Tests.PersonalBestsExclude),
    ("Records : nouveau record détecté, pas au premier essai", Tests.PersonalBestDetection),
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
        True(Formatting.Tooltip(row, "Joueur1", "MNO", 10, 10).Contains("estimés"), "l'infobulle doit le signaler");
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

    private static EncounterRecord Saved(string id, string category, string place, string character, double startOffset, long damage, double seconds, long localDamage = 0)
    {
        var encounter = new Encounter(At(startOffset), place);
        encounter.Add(Direct(1, damage), At(startOffset));
        encounter.Add(Direct(1, 1), At(startOffset + seconds));
        if (localDamage > 0)
            encounter.Add(new Hit(2, character, 25, true, "Boss", "Feu", localDamage), At(startOffset + seconds));
        encounter.End(At(startOffset + seconds));

        return EncounterRecord.From(encounter, category, place, character, At(startOffset + seconds)) with { Id = id };
    }

    private static string TemporaryDirectory()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dpsmeter-tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    private static EncounterStore Reload(string directory, Action<Exception, string>? onError = null)
    {
        var store = new EncounterStore(directory, onError);
        store.LoadInBackground();
        True(store.Flush(TimeSpan.FromSeconds(10)), "chargement trop long");
        True(store.IsLoaded, "chargement non terminé");
        return store;
    }

    public static void StoreRoundTrip()
    {
        var directory = TemporaryDirectory();
        var store = Reload(directory);

        var encounter = new Encounter(T0, "Zone");
        encounter.Add(Direct(1, 1000, crit: true), At(0));
        encounter.Add(Dot(2, 400, estimated: true), At(10));
        encounter.Add(Direct(3, 600, local: true, direct: true), At(20));
        encounter.End(At(25));
        var record = EncounterRecord.From(encounter, "Donjons", "Sastasha", "Moi", At(25));
        store.Add(record);
        store.Dispose();

        var reloaded = Reload(directory);
        Equal(1, reloaded.Count, "combats");
        var read = reloaded.All()[0];
        Equal(record.Id, read.Id, "identifiant");
        Equal("Donjons", read.Category, "type");
        Equal("Sastasha", read.Place, "lieu");
        Equal(EncounterRecord.CurrentFormat, read.FormatVersion, "format");
        Equal(record.View.StartUtc, read.View.StartUtc, "début");
        Equal(2000L, read.View.TotalDamage, "total");
        Near(record.View.TotalDps, read.View.TotalDps);
        Near(20, read.View.DurationSeconds);
        True(!read.View.IsActive, "un combat relu est terminé");
        Equal(3, read.View.Combatants.Count, "combattants");
        Equal(1u, read.View.Combatants[0].Id, "tri conservé");
        Equal(3u, read.View.Local!.Id, "ligne du joueur");
        Equal(400L, read.View.Combatants.Single(c => c.Id == 2).EstimatedDotDamage, "DoT estimés");
        Equal("Feu", read.View.Combatants[0].Actions[0].Name, "action");
        System.IO.Directory.Delete(directory, true);
    }

    public static void StoreKeepsAccents()
    {
        var directory = TemporaryDirectory();
        var store = Reload(directory);
        store.Add(Saved("a", "Défis", "Le Palais des Damnés", "Éloïse d'Été", 0, 100, 20));
        store.Dispose();

        var read = Reload(directory).All()[0];
        Equal("Défis", read.Category, "type");
        Equal("Le Palais des Damnés", read.Place, "lieu");
        Equal("Éloïse d'Été", read.CharacterName, "personnage");
        System.IO.Directory.Delete(directory, true);
    }

    public static void StoreSkipsCorruptFile()
    {
        var directory = TemporaryDirectory();
        var store = Reload(directory);
        store.Add(Saved("bon", "Donjons", "Zone", "Moi", 0, 100, 20));
        store.Dispose();

        var corrupt = System.IO.Path.Combine(directory, "abime.json");
        System.IO.File.WriteAllText(corrupt, "{ pas du json");
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "vide.json"), "{}");

        var errors = 0;
        var reloaded = Reload(directory, (_, _) => errors++);
        Equal(1, reloaded.Count, "combats lisibles");
        Equal(2, reloaded.UnreadableFiles, "fichiers illisibles");
        True(errors >= 1, "l'erreur doit être journalisée");
        True(System.IO.File.Exists(corrupt), "un fichier abîmé ne doit pas être effacé");
        System.IO.Directory.Delete(directory, true);
    }

    public static void StoreDeleteIsPermanent()
    {
        var directory = TemporaryDirectory();
        var store = Reload(directory);
        store.Add(Saved("a", "Donjons", "Zone", "Moi", 0, 100, 20));
        store.Add(Saved("b", "Donjons", "Zone", "Moi", 100, 100, 20));
        store.Add(Saved("c", "Donjons", "Zone", "Moi", 200, 100, 20));
        var before = store.Version;

        store.Delete(new[] { "a", "c", "inconnu" });
        Equal(1, store.Count, "supprimés en mémoire");
        True(store.Version != before, "l'affichage doit être prévenu");
        store.Dispose();

        var reloaded = Reload(directory);
        Equal(1, reloaded.Count, "supprimés sur le disque");
        Equal("b", reloaded.All()[0].Id, "le bon combat reste");
        System.IO.Directory.Delete(directory, true);
    }

    public static void StoreLeavesNoTemporaryFile()
    {
        var directory = TemporaryDirectory();
        var store = Reload(directory);
        store.Add(Saved("a", "Donjons", "Zone", "Moi", 0, 100, 20));
        store.Dispose();
        Equal(0, System.IO.Directory.GetFiles(directory, "*.tmp").Length, "fichiers temporaires");
        Equal(1, System.IO.Directory.GetFiles(directory, "*.json").Length, "fichiers de combat");
        System.IO.Directory.Delete(directory, true);
    }

    public static void StoreKeepsRecordAddedBeforeLoad()
    {
        var directory = TemporaryDirectory();
        var first = Reload(directory);
        first.Add(Saved("ancien", "Donjons", "Zone", "Moi", 0, 100, 20));
        first.Dispose();

        var store = new EncounterStore(directory);
        store.Add(Saved("nouveau", "Donjons", "Zone", "Moi", 100, 100, 20));
        store.LoadInBackground();
        True(store.Flush(TimeSpan.FromSeconds(10)), "chargement trop long");
        Equal(2, store.Count, "combats");
        System.IO.Directory.Delete(directory, true);
    }

    private static EncounterRecord[] QuerySample() => new[]
    {
        Saved("a", "Donjons", "Sastasha", "Moi", 0, 1000, 100, 500),
        Saved("b", "Donjons", "Tam-Tara", "Moi", 1000, 5000, 100, 100),
        Saved("c", "Raids", "Arcadion", "Alt", 2000, 9000, 300, 900),
        Saved("d", "Monde ouvert", "Limsa", "Moi", 3000, 100, 10),
    };

    private static string Ids(IEnumerable<EncounterRecord> records) => string.Concat(records.Select(r => r.Id));

    public static void QueryFilters()
    {
        var all = QuerySample();
        Equal(4, EncounterQuery.Apply(all, new EncounterFilter(), EncounterSort.Newest).Count, "aucun filtre");
        Equal("ba", Ids(EncounterQuery.Apply(all, new EncounterFilter("Donjons"), EncounterSort.Newest)), "type");
        Equal("a", Ids(EncounterQuery.Apply(all, new EncounterFilter("Donjons", "Sastasha"), EncounterSort.Newest)), "type et lieu");
        Equal("c", Ids(EncounterQuery.Apply(all, new EncounterFilter(Character: "Alt"), EncounterSort.Newest)), "personnage");
        Equal("", Ids(EncounterQuery.Apply(all, new EncounterFilter("Raids", "Sastasha"), EncounterSort.Newest)), "filtres sans résultat");
    }

    public static void QuerySorts()
    {
        var all = QuerySample();
        Equal("dcba", Ids(EncounterQuery.Apply(all, new EncounterFilter(), EncounterSort.Newest)), "récents");
        Equal("abcd", Ids(EncounterQuery.Apply(all, new EncounterFilter(), EncounterSort.Oldest)), "anciens");
        Equal("c", EncounterQuery.Apply(all, new EncounterFilter(), EncounterSort.Longest)[0].Id, "plus long");
        Equal("b", EncounterQuery.Apply(all, new EncounterFilter("Donjons"), EncounterSort.BestGroupDps)[0].Id, "meilleur groupe");

        // Un combat sans ligne du joueur passe après tous les autres.
        var byLocal = EncounterQuery.Apply(all, new EncounterFilter(), EncounterSort.BestLocalDps);
        Equal("a", byLocal[0].Id, "mon meilleur DPS");
        Equal("d", byLocal[3].Id, "sans moi en dernier");
    }

    public static void QueryOptions()
    {
        var options = EncounterQuery.Options(QuerySample(), r => r.Category);
        Equal(3, options.Count, "types");
        Equal("Donjons", options[0].Value, "ordre");
        Equal(2, options[0].Count, "nombre");
        Equal(0, EncounterQuery.Options(new[] { Saved("x", "", "", "", 0, 1, 10) }, r => r.Category).Count, "valeur vide ignorée");
    }

    public static void StoreReadsVersionOneFile()
    {
        // Copie conforme d'un combat écrit par la v0.2 : c'est ce qui se trouve déjà sur le disque des joueurs.
        const string json = """
            {"FormatVersion":1,"Id":"20261102-220000-abcd1234","Category":"Donjons","Place":"Sastasha","CharacterName":"Shinra Stein",
            "View":{"Title":"Denn","ZoneName":"Sastasha","IsActive":false,"StartUtc":"2026-11-02T21:00:00Z","DurationSeconds":120.5,
            "TotalDamage":240000,"TotalDps":1991.7,"Combatants":[{"Id":268,"Name":"Shinra Stein","JobId":25,"IsLocal":true,"Damage":240000,
            "Dps":1991.7,"Share":1,"Hits":50,"CritRate":0.2,"DirectHitRate":0.3,"DotDamage":1000,"EstimatedDotDamage":0,
            "TopActions":[{"Name":"Feu","Damage":100000,"Hits":10,"Share":0.41}]}]}}
            """;

        var directory = TemporaryDirectory();
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "20261102-220000-abcd1234.json"), json);

        var errors = 0;
        var store = Reload(directory, (_, _) => errors++);
        Equal(0, errors, "erreurs");
        Equal(0, store.UnreadableFiles, "illisibles");
        Equal(1, store.Count, "combats");

        var read = store.All()[0];
        var row = read.View.Local!;
        Equal(Outcome.Unknown, read.View.Outcome, "issue");
        Equal("Feu", row.Actions[0].Name, "action relue depuis TopActions");
        Equal(100000L, row.Actions[0].Damage, "dégâts de l'action");
        Equal(0L, row.Actions[0].MaxHit, "plus gros coup absent");
        Equal(0L, row.Healing, "soins absents");
        Equal(0, row.HealActions!.Count, "liste de soins vide");
        Equal(0, row.Timeline!.Count, "courbe vide");
        Equal(0, row.Deaths, "morts absentes");
        True(!read.View.HasTimeline, "pas de courbe");

        // Un combat relu puis réécrit reste lisible.
        store.Add(read);
        store.Dispose();
        Equal("Feu", Reload(directory).All()[0].View.Local!.Actions[0].Name, "après réécriture");
        System.IO.Directory.Delete(directory, true);
    }

    public static void StoreKeepsFullDetail()
    {
        var directory = TemporaryDirectory();
        var store = Reload(directory);
        var encounter = new Encounter(T0, "Zone");
        for (var i = 0; i < 12; i++)
            encounter.Add(new Hit(1, "Moi", 25, true, "Boss", $"Action{i}", 100 + i, TargetId: 7, TargetHp: 0.5f), At(i * 3));
        encounter.AddHealing(new Hit(1, "Moi", 25, true, "Moi", "Soin", 500));
        encounter.AddDamageTaken(new Hit(1, "Moi", 25, true, "Moi", "Coup", 300));
        encounter.AddDeath(new Hit(1, "Moi", 25, true, "", "", 0), At(10));
        encounter.End(At(40));
        store.Add(EncounterRecord.From(encounter, "Donjons", "Zone", "Moi", At(40)));
        store.Dispose();

        var read = Reload(directory).All()[0];
        var row = read.View.Local!;
        Equal(12, row.Actions.Count, "actions");
        Equal(111L, row.Actions[0].MaxHit, "plus gros coup");
        Equal("Soin", row.HealActions![0].Name, "soin");
        Equal(500L, row.Healing, "soins");
        Equal(300L, row.DamageTaken, "subis");
        Equal(1, row.Deaths, "morts");
        Near(10, row.DeathSeconds![0]);
        True(row.Timeline!.Count >= 7, "courbe");
        Equal(Outcome.Failure, read.View.Outcome, "issue");
        Near(0.5, read.View.RemainingHp);
        Equal(500L, read.View.TotalHealing, "soins du groupe");
        System.IO.Directory.Delete(directory, true);
    }

    public static void AllActionsAreKept()
    {
        var encounter = new Encounter(T0, "Zone");
        for (var i = 0; i < 20; i++)
            encounter.Add(new Hit(1, "Moi", 25, true, "Boss", $"Action{i}", 100), At(1));
        encounter.Add(new Hit(1, "Moi", 25, true, "Boss", "Action3", 900), At(2));

        var row = EncounterView.From(encounter, At(3)).Combatants[0];
        Equal(20, row.Actions.Count, "actions");
        Equal("Action3", row.Actions[0].Name, "tri");
        Equal(1000L, row.Actions[0].Damage, "total");
        Equal(900L, row.Actions[0].MaxHit, "plus gros coup");
        Equal(2, row.Actions[0].Hits, "coups");
    }

    public static void TimelineBuckets()
    {
        var encounter = new Encounter(T0, "Zone");
        encounter.Add(Direct(1, 100), At(0));
        encounter.Add(Direct(1, 200), At(4.9));
        encounter.Add(Direct(1, 400), At(5));
        encounter.Add(Direct(1, 800), At(17));

        var view = EncounterView.From(encounter, At(20));
        var timeline = view.Combatants[0].Timeline!;
        Equal(4, timeline.Count, "points");
        Equal(300L, timeline[0], "0-5 s");
        Equal(400L, timeline[1], "5-10 s");
        Equal(0L, timeline[2], "10-15 s sans dégâts");
        Equal(800L, timeline[3], "15-20 s");
        Equal(1500L, timeline.Sum(), "la courbe contient tous les dégâts");
    }

    public static void TimelineLastPointIsScaled()
    {
        var encounter = new Encounter(T0, "Zone");
        encounter.Add(Direct(1, 500), At(0));
        encounter.Add(Direct(1, 200), At(6));

        var view = EncounterView.From(encounter, At(7));
        var row = view.Combatants[0];
        Near(100, view.TimelineDps(row, 0));
        Near(100, view.TimelineDps(row, 1)); // 200 dégâts sur les 2 secondes couvertes, pas sur 5
        Near(0, view.TimelineDps(row, 99));
    }

    public static void RecentDpsUsesWindow()
    {
        var encounter = new Encounter(T0, "Zone");
        encounter.Add(Direct(1, 100000), At(0)); // ouverture, hors de la fenêtre
        for (var i = 20; i < 30; i++)
            encounter.Add(Direct(1, 500), At(i));

        var row = EncounterView.From(encounter, At(30), 10).Combatants[0];
        Near(500, row.RecentDps);
        True(row.Dps > 3000, "la moyenne garde l'ouverture");
    }

    public static void RecentDpsAtStart()
    {
        var encounter = new Encounter(T0, "Zone");
        encounter.Add(Direct(1, 1000), At(0));
        encounter.Add(Direct(1, 1000), At(1));
        var row = EncounterView.From(encounter, At(2), 10).Combatants[0];
        Near(1000, row.RecentDps);
    }

    public static void RecentDpsDecays()
    {
        var encounter = new Encounter(T0, "Zone");
        encounter.Add(Direct(1, 1000), At(0));
        Near(0, EncounterView.From(encounter, At(60), 10).Combatants[0].RecentDps);

        // Une case réutilisée (même seconde modulo la taille) ne doit pas relire d'anciens dégâts.
        var combatant = new Combatant(1, "Moi", 25, true);
        combatant.AddDamage(Direct(1, 1000), 3);
        Near(0, combatant.RecentDps(3 + Combatant.MaxRecentWindow + 1, 5));
    }

    public static void RecentDpsZeroWhenEnded()
    {
        var encounter = new Encounter(T0, "Zone");
        encounter.Add(Direct(1, 1000), At(0));
        encounter.Add(Direct(1, 1000), At(5));
        encounter.End(At(6));
        Near(0, EncounterView.From(encounter, At(6), 10).Combatants[0].RecentDps);
    }

    private static Hit OnTarget(uint targetId, long amount, float hp) =>
        new(1, "Moi", 25, true, "Boss", "Feu", amount, TargetId: targetId, TargetHp: hp);

    public static void OutcomeVictoryOnDeath()
    {
        var encounter = new Encounter(T0, "Zone");
        encounter.Add(OnTarget(7, 100, 0.4f), At(1));
        encounter.MarkTargetDead(7);
        encounter.End(At(2));
        Equal(Outcome.Victory, encounter.Outcome, "issue");
        True(encounter.RemainingHp < 0, "pas de PV restants");
    }

    public static void OutcomeVictoryOnZeroHp()
    {
        var encounter = new Encounter(T0, "Zone");
        encounter.Add(OnTarget(7, 100, 0.4f), At(1));
        encounter.UpdateTargetHp(7, 0f);
        encounter.End(At(2));
        Equal(Outcome.Victory, encounter.Outcome, "issue");
    }

    public static void OutcomeFailureKeepsHp()
    {
        var encounter = new Encounter(T0, "Zone");
        encounter.Add(OnTarget(7, 100, 0.123f), At(1));
        encounter.End(At(2));
        Equal(Outcome.Failure, encounter.Outcome, "issue");
        Near(0.123, encounter.RemainingHp, 0.001);
        Equal("Échec  |  cible à 12,3 %", Formatting.OutcomeText(encounter.Outcome, encounter.RemainingHp), "texte");
    }

    public static void OutcomeInterruptedOnDummy()
    {
        var encounter = new Encounter(T0, "Zone");
        encounter.Add(OnTarget(7, 100, 1f), At(1));
        encounter.End(At(2));
        Equal(Outcome.Interrupted, encounter.Outcome, "issue");
    }

    public static void OutcomeUnknownWithoutHp()
    {
        var withoutTarget = new Encounter(T0, "Zone");
        withoutTarget.Add(Direct(1, 100), At(1));
        withoutTarget.End(At(2));
        Equal(Outcome.Unknown, withoutTarget.Outcome, "sans cible");

        var withoutHp = new Encounter(T0, "Zone");
        withoutHp.Add(OnTarget(7, 100, -1f), At(1));
        withoutHp.End(At(2));
        Equal(Outcome.Unknown, withoutHp.Outcome, "sans PV");
    }

    public static void OutcomeMarkWins()
    {
        var wiped = new Encounter(T0, "Zone");
        wiped.Add(OnTarget(7, 100, 0.3f), At(1));
        wiped.MarkTargetDead(7);
        wiped.MarkOutcome(Outcome.Failure);
        wiped.End(At(2));
        Equal(Outcome.Failure, wiped.Outcome, "équipe vaincue");

        var completed = new Encounter(T0, "Zone");
        completed.Add(OnTarget(7, 100, 0.3f), At(1));
        completed.MarkOutcome(Outcome.Victory);
        completed.End(At(2));
        Equal(Outcome.Victory, completed.Outcome, "instance terminée");
    }

    public static void OutcomeUsesMainTarget()
    {
        var encounter = new Encounter(T0, "Zone");
        encounter.Add(OnTarget(7, 900, 0.5f), At(1));
        encounter.Add(OnTarget(8, 100, 0.5f), At(1));
        encounter.MarkTargetDead(8); // un sbire meurt, le boss reste
        Equal(7u, encounter.MainTargetId, "cible principale");
        encounter.End(At(2));
        Equal(Outcome.Failure, encounter.Outcome, "issue");
    }

    public static void OutcomeLateMark()
    {
        var tracker = new EncounterTracker();
        tracker.Record(OnTarget(7, 100, 0.3f), T0, "Zone");
        tracker.EndCurrent(At(5));
        Equal(Outcome.Failure, tracker.Displayed!.Outcome, "avant l'annonce");
        Near(0.3, tracker.Displayed!.RemainingHp, 0.001);

        var marked = tracker.MarkLastEnded(Outcome.Victory, At(30), 60);
        True(marked != null, "dans le délai");
        Equal(Outcome.Victory, tracker.Displayed!.Outcome, "après l'annonce");
        True(tracker.Displayed!.RemainingHp < 0, "plus de PV restants après une victoire");

        True(tracker.MarkLastEnded(Outcome.Failure, At(500), 60) == null, "trop tard");
        Equal(Outcome.Victory, tracker.Displayed!.Outcome, "inchangé");
    }

    public static void SupportStatsAreCounted()
    {
        var tracker = new EncounterTracker();
        tracker.Record(Direct(1, 100), T0, "Zone");
        tracker.RecordHealing(new Hit(2, "Soigneur", 24, false, "Moi", "Soin", 700, IsCrit: true));
        tracker.RecordHealing(new Hit(2, "Soigneur", 24, false, "Moi", "Soin", 300));
        tracker.RecordDamageTaken(new Hit(1, "Joueur1", 25, false, "Joueur1", "Coup", 400));
        tracker.RecordDeath(new Hit(1, "Joueur1", 25, false, "", "", 0), At(5));

        var view = tracker.BuildView(tracker.Displayed, At(10))!;
        Equal(1000L, view.TotalHealing, "soins du groupe");
        Equal(400L, view.TotalDamageTaken, "subis du groupe");
        var healer = view.Combatants.Single(c => c.Id == 2);
        Equal(1000L, healer.Healing, "soins");
        Equal(0L, healer.Damage, "un soigneur sans dégâts a quand même sa ligne");
        Equal(2, healer.HealActions![0].Hits, "soins lancés");
        Equal(1, view.Combatants.Single(c => c.Id == 1).Deaths, "morts");
        Near(5, view.Combatants.Single(c => c.Id == 1).DeathSeconds![0]);
    }

    public static void SupportStatsDoNotStart()
    {
        var tracker = new EncounterTracker();
        tracker.RecordHealing(new Hit(2, "Soigneur", 24, false, "Moi", "Soin", 700));
        tracker.RecordDamageTaken(new Hit(1, "Joueur1", 25, false, "Joueur1", "Coup", 400));
        tracker.RecordDeath(new Hit(1, "Joueur1", 25, false, "", "", 0), T0);
        tracker.RecordTargetDeath(7);
        tracker.UpdateTargetHp(7, 0.5f);
        True(!tracker.MarkOutcome(Outcome.Victory), "aucun combat à marquer");
        True(!tracker.IsActive, "aucun combat ne doit démarrer");
        True(tracker.Displayed == null, "rien à afficher");
    }

    public static void RowsForMetric()
    {
        var encounter = new Encounter(T0, "Zone");
        encounter.Add(Direct(1, 1000), At(1));
        encounter.Add(Direct(2, 500), At(1));
        encounter.AddHealing(new Hit(2, "Joueur2", 25, false, "", "Soin", 900));
        encounter.AddHealing(new Hit(3, "Joueur3", 24, false, "", "Soin", 1500));
        encounter.AddDamageTaken(new Hit(1, "Joueur1", 25, false, "", "Coup", 50));

        var view = EncounterView.From(encounter, At(10));
        Equal("1,2", string.Join(",", view.RowsFor(MeterMetric.Damage).Select(r => r.Id)), "dégâts");
        Equal("3,2", string.Join(",", view.RowsFor(MeterMetric.Healing).Select(r => r.Id)), "soins");
        Equal("1", string.Join(",", view.RowsFor(MeterMetric.DamageTaken).Select(r => r.Id)), "subis");
        Equal(2400L, view.TotalOf(MeterMetric.Healing), "total des soins");
    }

    private static EncounterRecord Fight(string id, string title, uint job, double startOffset, double seconds, long damage, Outcome outcome, string character = "Moi")
    {
        var encounter = new Encounter(At(startOffset), "Zone");
        encounter.Add(new Hit(1, character, job, true, title, "Feu", damage, TargetId: 7, TargetHp: 0.5f), At(startOffset));
        encounter.Add(new Hit(1, character, job, true, title, "Feu", 1, TargetId: 7, TargetHp: 0.5f), At(startOffset + seconds));
        encounter.MarkOutcome(outcome);
        encounter.End(At(startOffset + seconds));
        return EncounterRecord.From(encounter, "Raids", "Arène", character, At(startOffset + seconds)) with { Id = id };
    }

    public static void QueryFiltersJobAndOutcome()
    {
        var all = new[]
        {
            Fight("a", "Boss", 25, 0, 60, 6000, Outcome.Victory),
            Fight("b", "Boss", 25, 100, 60, 6000, Outcome.Failure),
            Fight("c", "Boss", 27, 200, 60, 6000, Outcome.Victory),
        };
        Equal("ba", Ids(EncounterQuery.Apply(all, new EncounterFilter(JobId: 25), EncounterSort.Newest)), "job");
        Equal("ca", Ids(EncounterQuery.Apply(all, new EncounterFilter(Outcome: Outcome.Victory), EncounterSort.Newest)), "issue");
        Equal("a", Ids(EncounterQuery.Apply(all, new EncounterFilter(JobId: 25, Outcome: Outcome.Victory), EncounterSort.Newest)), "les deux");
    }

    public static void PersonalBestsCompute()
    {
        var bests = PersonalBests.Compute(new[]
        {
            Fight("a", "Boss", 25, 0, 100, 10000, Outcome.Victory),
            Fight("b", "Boss", 25, 200, 100, 30000, Outcome.Victory),
            Fight("c", "Boss", 25, 400, 100, 20000, Outcome.Victory),
            Fight("d", "Boss", 27, 600, 100, 5000, Outcome.Victory),
            Fight("e", "Autre", 25, 800, 100, 1000, Outcome.Victory),
            Fight("f", "Boss", 25, 1000, 100, 90000, Outcome.Victory, "Alt"),
        });

        Equal(4, bests.Count, "records");
        Equal("f", bests[0].BestRecordId, "du plus récent au plus ancien");
        var mage = bests.Single(b => b.Title == "Boss" && b.JobId == 25 && b.CharacterName == "Moi");
        Equal("b", mage.BestRecordId, "meilleur combat");
        Near(300.01, mage.BestDps);
        Near(200.01, mage.LastDps);
        Equal(3, mage.Attempts, "essais");
        Near(100.01, mage.Progress[0]);
        Near(200.01, mage.Progress[2]);
    }

    public static void PersonalBestsExclude()
    {
        var short_ = Fight("a", "Boss", 25, 0, 10, 90000, Outcome.Victory);
        var failed = Fight("b", "Boss", 25, 100, 100, 90000, Outcome.Failure);
        var dummy = Fight("c", "Boss", 25, 300, 100, 5000, Outcome.Interrupted);
        var recent = Fight("d", "Boss", 25, 500, 100, 7000, Outcome.Victory);
        var old = recent with { View = recent.View with { Outcome = Outcome.Unknown } };
        True(!PersonalBests.IsEligible(short_), "trop court");
        True(!PersonalBests.IsEligible(failed), "échec");
        True(PersonalBests.IsEligible(dummy), "mannequin");
        True(PersonalBests.IsEligible(old), "ancien combat sans issue");
        Equal(1, PersonalBests.Compute(new[] { short_, failed }.Append(dummy)).Count, "seul le mannequin compte");

        var encounter = new Encounter(T0, "Zone");
        encounter.Add(Direct(2, 5000), At(0));
        encounter.Add(Direct(2, 5000), At(60));
        encounter.End(At(60));
        True(!PersonalBests.IsEligible(EncounterRecord.From(encounter, "Raids", "Arène", "Moi", At(60))), "sans ligne du joueur");
    }

    public static void PersonalBestDetection()
    {
        var first = Fight("a", "Boss", 25, 0, 100, 10000, Outcome.Victory);
        True(!PersonalBests.BeatsPrevious(first, Array.Empty<EncounterRecord>()), "premier essai");

        var better = Fight("b", "Boss", 25, 200, 100, 20000, Outcome.Victory);
        True(PersonalBests.BeatsPrevious(better, new[] { first }), "meilleur");

        var worse = Fight("c", "Boss", 25, 400, 100, 5000, Outcome.Victory);
        True(!PersonalBests.BeatsPrevious(worse, new[] { first, better }), "moins bon");

        var otherJob = Fight("d", "Boss", 27, 600, 100, 90000, Outcome.Victory);
        True(!PersonalBests.BeatsPrevious(otherJob, new[] { first, better }), "autre job : premier essai");

        var failed = Fight("e", "Boss", 25, 800, 100, 90000, Outcome.Failure);
        True(!PersonalBests.BeatsPrevious(failed, new[] { first, better }), "échec");
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
        True(!Formatting.Tooltip(row, row.Name, "MNO", 195, 10).Contains('\u2014'), "tiret long dans l'infobulle");
    }
}
