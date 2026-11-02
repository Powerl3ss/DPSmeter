using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace DPSmeter.Core;

/// <summary>Un combat enregistré : sa photo finale, et de quoi le retrouver.</summary>
public sealed record EncounterRecord(
    int FormatVersion,
    string Id,
    string Category,
    string Place,
    string CharacterName,
    EncounterView View,
    int WipeNumber = 0,
    string RunId = "")
{
    public const int CurrentFormat = 4;

    public static EncounterRecord From(Encounter encounter, string category, string place, string characterName, DateTime nowUtc, int wipeNumber = 0, string runId = "")
    {
        var view = EncounterView.From(encounter, nowUtc);
        var id = $"{encounter.StartUtc:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        return new EncounterRecord(CurrentFormat, id, category, place, view.Local?.Name ?? characterName, view, wipeNumber, runId);
    }
}

/// <summary>
/// Une entrée de l'historique : un passage dans une instance, avec tous ses essais (wipes compris).
/// Hors instance, et pour les combats enregistrés avant la v0.5, une entrée contient un seul combat.
/// </summary>
public sealed record EncounterRun(string Id, IReadOnlyList<EncounterRecord> Attempts)
{
    public EncounterRecord First => Attempts[0];
    public EncounterRecord Last => Attempts[^1];

    public string Category => First.Category;
    public string Place => First.Place;
    public string CharacterName => First.CharacterName;
    public DateTime StartUtc => First.View.StartUtc;

    /// <summary>Le nom de l'instance, ou celui de l'adversaire pour un combat isolé.</summary>
    public string Title => First.RunId.Length > 0 && First.Place.Length > 0 ? First.Place : First.View.Title;

    public int Wipes => Attempts.Count(a => a.WipeNumber > 0);
    public double DurationSeconds => Attempts.Sum(a => a.View.DurationSeconds);
    public long TotalDamage => Attempts.Sum(a => a.View.TotalDamage);
    public double BestGroupDps => Attempts.Max(a => a.View.TotalDps);
    public double BestLocalDps => Attempts.Max(a => a.View.Local?.Dps ?? -1);

    /// <summary>Job joué, d'après le premier essai où le joueur apparaît (0 si inconnu).</summary>
    public uint LocalJobId => Attempts.Select(a => a.View.Local?.JobId ?? 0).FirstOrDefault(job => job != 0);

    /// <summary>Une victoire l'emporte sur les wipes qui l'ont précédée. Sinon, c'est le dernier essai qui compte.</summary>
    public Outcome Outcome => Attempts.Any(a => a.View.Outcome == Outcome.Victory) ? Outcome.Victory : Last.View.Outcome;

    public bool Contains(string recordId) => Attempts.Any(a => a.Id == recordId);
}

/// <summary>
/// Les combats enregistrés sur le disque, un fichier par combat : un fichier abîmé n'affecte que son combat,
/// et supprimer un combat ne réécrit pas les autres. Lectures et écritures passent par une file
/// en arrière-plan pour ne jamais ralentir le jeu.
/// </summary>
public sealed class EncounterStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);

    private readonly object gate = new();
    private readonly string directory;
    private readonly Action<Exception, string>? onError;
    private readonly Dictionary<string, EncounterRecord> records = new();
    private Task queue = Task.CompletedTask;
    private int version;
    private int unreadableFiles;
    private bool isLoaded;

    public EncounterStore(string directory, Action<Exception, string>? onError = null)
    {
        this.directory = directory;
        this.onError = onError;
    }

    /// <summary>Change à chaque modification : l'affichage sait ainsi quand recalculer sa liste.</summary>
    public int Version
    {
        get
        {
            lock (gate)
                return version;
        }
    }

    public bool IsLoaded
    {
        get
        {
            lock (gate)
                return isLoaded;
        }
    }

    public int Count
    {
        get
        {
            lock (gate)
                return records.Count;
        }
    }

    /// <summary>Fichiers illisibles au chargement. Ils restent sur le disque, sans être touchés.</summary>
    public int UnreadableFiles
    {
        get
        {
            lock (gate)
                return unreadableFiles;
        }
    }

    public EncounterRecord[] All()
    {
        lock (gate)
            return records.Values.ToArray();
    }

    public void LoadInBackground() => Enqueue("Chargement des combats", Load);

    public void Add(EncounterRecord record)
    {
        lock (gate)
        {
            records[record.Id] = record;
            version++;
        }

        Enqueue($"Enregistrement du combat {record.Id}", () => Write(record));
    }

    public void Delete(IEnumerable<string> ids)
    {
        var removed = new List<string>();
        lock (gate)
        {
            foreach (var id in ids)
            {
                if (records.Remove(id))
                    removed.Add(id);
            }

            if (removed.Count > 0)
                version++;
        }

        foreach (var id in removed)
            Enqueue($"Suppression du combat {id}", () => File.Delete(PathOf(id)));
    }

    /// <summary>Attend la fin des lectures et écritures en cours.</summary>
    public bool Flush(TimeSpan timeout)
    {
        Task pending;
        lock (gate)
            pending = queue;
        return pending.Wait(timeout);
    }

    public void Dispose() => Flush(ShutdownWait);

    private string PathOf(string id) => Path.Combine(directory, id + ".json");

    private void Enqueue(string description, Action work)
    {
        lock (gate)
        {
            queue = queue.ContinueWith(_ =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    onError?.Invoke(ex, description);
                }
            }, TaskScheduler.Default);
        }
    }

    private void Load()
    {
        Directory.CreateDirectory(directory);

        var loaded = new List<EncounterRecord>();
        var unreadable = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<EncounterRecord>(File.ReadAllText(file), JsonOptions);
                if (record?.View?.Combatants == null)
                {
                    unreadable++;
                    continue;
                }

                // Le nom du fichier fait foi : il sert ensuite à retrouver le fichier à supprimer.
                loaded.Add(record with { Id = Path.GetFileNameWithoutExtension(file), View = record.View.Normalized() });
            }
            catch (Exception ex)
            {
                unreadable++;
                onError?.Invoke(ex, $"Lecture de {Path.GetFileName(file)}");
            }
        }

        lock (gate)
        {
            foreach (var record in loaded)
                records.TryAdd(record.Id, record);
            unreadableFiles = unreadable;
            isLoaded = true;
            version++;
        }
    }

    private void Write(EncounterRecord record)
    {
        Directory.CreateDirectory(directory);

        // Écriture dans un fichier temporaire : un arrêt du jeu en plein milieu ne laisse pas de combat tronqué.
        var path = PathOf(record.Id);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }
}

public enum EncounterSort
{
    Newest,
    Oldest,
    BestGroupDps,
    BestLocalDps,
    Longest,
}

/// <summary>Filtres de la page Historique. Une valeur vide (chaîne vide, job 0, issue absente) veut dire « tous ».</summary>
public readonly record struct EncounterFilter(
    string Category = "",
    string Place = "",
    string Character = "",
    uint JobId = 0,
    Outcome? Outcome = null)
{
    public bool Matches(EncounterRun run) =>
        (string.IsNullOrEmpty(Category) || run.Category == Category) &&
        (string.IsNullOrEmpty(Place) || run.Place == Place) &&
        (string.IsNullOrEmpty(Character) || run.CharacterName == Character) &&
        (JobId == 0 || run.LocalJobId == JobId) &&
        (Outcome == null || run.Outcome == Outcome);
}

/// <summary>Filtres et tris de la page Historique.</summary>
public static class EncounterQuery
{
    public static string SortLabel(EncounterSort sort) => sort switch
    {
        EncounterSort.Newest => "Plus récents",
        EncounterSort.Oldest => "Plus anciens",
        EncounterSort.BestGroupDps => "Meilleur DPS du groupe",
        EncounterSort.BestLocalDps => "Mon meilleur DPS",
        EncounterSort.Longest => "Plus longs",
        _ => string.Empty,
    };

    /// <summary>Range les combats par passage dans une instance, les essais du plus ancien au plus récent.</summary>
    public static List<EncounterRun> Group(IEnumerable<EncounterRecord> records) =>
        records.GroupBy(r => r.RunId.Length > 0 ? r.RunId : r.Id)
               .Select(g => new EncounterRun(g.Key, g.OrderBy(r => r.View.StartUtc).ThenBy(r => r.Id, StringComparer.Ordinal).ToList()))
               .ToList();

    public static List<EncounterRun> Apply(IEnumerable<EncounterRecord> records, EncounterFilter filter, EncounterSort sort) =>
        Sort(Group(records).Where(filter.Matches), sort);

    public static List<EncounterRun> Sort(IEnumerable<EncounterRun> runs, EncounterSort sort)
    {
        // À égalité, le plus récent d'abord : l'ordre reste stable d'une image à l'autre.
        var sorted = sort switch
        {
            EncounterSort.Oldest => runs.OrderBy(r => r.StartUtc),
            EncounterSort.BestGroupDps => runs.OrderByDescending(r => r.BestGroupDps),
            EncounterSort.BestLocalDps => runs.OrderByDescending(r => r.BestLocalDps),
            EncounterSort.Longest => runs.OrderByDescending(r => r.DurationSeconds),
            _ => runs.OrderByDescending(r => r.StartUtc),
        };

        return sorted.ThenByDescending(r => r.StartUtc).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Valeurs possibles d'un filtre et leur nombre d'entrées, par ordre alphabétique.</summary>
    public static List<(string Value, int Count)> Options<T>(IEnumerable<T> items, Func<T, string> key) =>
        items.GroupBy(key)
             .Where(g => g.Key.Length > 0)
             .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
             .Select(g => (g.Key, g.Count()))
             .ToList();
}

/// <summary>Ton meilleur DPS sur un adversaire, avec un job et un personnage.</summary>
public sealed record PersonalBest(
    string Title,
    string Place,
    string Category,
    string CharacterName,
    uint JobId,
    double BestDps,
    string BestRecordId,
    DateTime BestDateUtc,
    DateTime LastDateUtc,
    double LastDps,
    IReadOnlyList<double> Progress)
{
    public int Attempts => Progress.Count;
}

/// <summary>Calcule les records personnels à partir des combats enregistrés.</summary>
public static class PersonalBests
{
    /// <summary>En dessous, le DPS d'un combat n'est pas comparable : une ouverture seule le gonfle.</summary>
    public const double MinDurationSeconds = 30;

    /// <summary>Un échec ne compte pas : mourir vite après son ouverture donnerait un faux record.</summary>
    public static bool IsEligible(EncounterRecord record) =>
        record.View.Local is { Dps: > 0 } &&
        record.View.DurationSeconds >= MinDurationSeconds &&
        record.View.Outcome != Outcome.Failure;

    /// <summary>Un record par adversaire, job et personnage, du plus récemment joué au plus ancien.</summary>
    public static List<PersonalBest> Compute(IEnumerable<EncounterRecord> records) =>
        records.Where(IsEligible)
               .GroupBy(r => (r.View.Title, r.Place, r.CharacterName, r.View.Local!.JobId))
               .Select(group =>
               {
                   var attempts = group.OrderBy(r => r.View.StartUtc).ToList();
                   var best = attempts.OrderByDescending(r => r.View.Local!.Dps).ThenBy(r => r.View.StartUtc).First();
                   var last = attempts[^1];
                   return new PersonalBest(
                       group.Key.Title, group.Key.Place, last.Category, group.Key.CharacterName, group.Key.JobId,
                       best.View.Local!.Dps, best.Id, best.View.StartUtc,
                       last.View.StartUtc, last.View.Local!.Dps,
                       attempts.Select(r => r.View.Local!.Dps).ToList());
               })
               .OrderByDescending(b => b.LastDateUtc)
               .ThenBy(b => b.BestRecordId, StringComparer.Ordinal)
               .ToList();

    /// <summary>Le combat bat-il un record déjà établi ? Le tout premier essai n'en bat aucun.</summary>
    public static bool BeatsPrevious(EncounterRecord record, IEnumerable<EncounterRecord> previous)
    {
        if (!IsEligible(record))
            return false;

        var local = record.View.Local!;
        var best = previous
            .Where(r => r.Id != record.Id && IsEligible(r) && r.View.Title == record.View.Title && r.Place == record.Place &&
                        r.CharacterName == record.CharacterName && r.View.Local!.JobId == local.JobId)
            .Select(r => (double?)r.View.Local!.Dps)
            .Max();
        return best != null && local.Dps > best;
    }
}
