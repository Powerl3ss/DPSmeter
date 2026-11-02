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
    EncounterView View)
{
    public const int CurrentFormat = 1;

    public static EncounterRecord From(Encounter encounter, string category, string place, string characterName, DateTime nowUtc)
    {
        var view = EncounterView.From(encounter, nowUtc);
        var id = $"{encounter.StartUtc:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        return new EncounterRecord(CurrentFormat, id, category, place, view.Local?.Name ?? characterName, view);
    }
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
                loaded.Add(record with { Id = Path.GetFileNameWithoutExtension(file) });
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

/// <summary>Filtres et tris de la page Historique. Une chaîne vide veut dire « tous ».</summary>
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

    public static List<EncounterRecord> Apply(IEnumerable<EncounterRecord> records, string category, string place, string character, EncounterSort sort)
    {
        var filtered = records.Where(r =>
            (category.Length == 0 || r.Category == category) &&
            (place.Length == 0 || r.Place == place) &&
            (character.Length == 0 || r.CharacterName == character));

        // À égalité, le plus récent d'abord : l'ordre reste stable d'une image à l'autre.
        var sorted = sort switch
        {
            EncounterSort.Oldest => filtered.OrderBy(r => r.View.StartUtc),
            EncounterSort.BestGroupDps => filtered.OrderByDescending(r => r.View.TotalDps),
            EncounterSort.BestLocalDps => filtered.OrderByDescending(r => r.View.Local?.Dps ?? -1),
            EncounterSort.Longest => filtered.OrderByDescending(r => r.View.DurationSeconds),
            _ => filtered.OrderByDescending(r => r.View.StartUtc),
        };

        return sorted.ThenByDescending(r => r.View.StartUtc).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Valeurs possibles d'un filtre et leur nombre de combats, par ordre alphabétique.</summary>
    public static List<(string Value, int Count)> Options(IEnumerable<EncounterRecord> records, Func<EncounterRecord, string> key) =>
        records.GroupBy(key)
               .Where(g => g.Key.Length > 0)
               .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
               .Select(g => (g.Key, g.Count()))
               .ToList();
}
