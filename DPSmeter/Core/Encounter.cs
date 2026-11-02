using System;
using System.Collections.Generic;
using System.Linq;

namespace DPSmeter.Core;

/// <summary>Un coup qui compte pour le DPS : déjà attribué à une ligne du tableau.</summary>
public readonly record struct Hit(
    uint SourceId,
    string SourceName,
    uint JobId,
    bool IsLocal,
    string TargetName,
    string ActionName,
    long Amount,
    bool IsCrit = false,
    bool IsDirectHit = false,
    bool IsDot = false,
    bool IsEstimated = false);

/// <summary>Total d'une action pour un combattant.</summary>
public sealed class ActionTotal
{
    public string Name { get; }
    public long Damage { get; set; }
    public int Hits { get; set; }

    public ActionTotal(string name) => Name = name;
}

/// <summary>Tout ce qu'un joueur (ou une ligne spéciale) a fait pendant un combat.</summary>
public sealed class Combatant
{
    public uint Id { get; }
    public string Name { get; set; }
    public uint JobId { get; set; }
    public bool IsLocal { get; set; }

    public long Damage { get; private set; }
    public long DotDamage { get; private set; }
    public long EstimatedDotDamage { get; private set; }
    public int Hits { get; private set; }
    public int Crits { get; private set; }
    public int DirectHits { get; private set; }

    private readonly Dictionary<string, ActionTotal> actions = new();
    public IEnumerable<ActionTotal> Actions => actions.Values;

    public Combatant(uint id, string name, uint jobId, bool isLocal)
    {
        Id = id;
        Name = name;
        JobId = jobId;
        IsLocal = isLocal;
    }

    public void Add(in Hit hit)
    {
        Damage += hit.Amount;

        // Les taux de critiques ne regardent que les coups directs : le jeu ne le dit pas pour les DoT.
        if (hit.IsDot)
        {
            DotDamage += hit.Amount;
            if (hit.IsEstimated)
                EstimatedDotDamage += hit.Amount;
        }
        else
        {
            Hits++;
            if (hit.IsCrit)
                Crits++;
            if (hit.IsDirectHit)
                DirectHits++;
        }

        if (!actions.TryGetValue(hit.ActionName, out var action))
            actions[hit.ActionName] = action = new ActionTotal(hit.ActionName);
        action.Damage += hit.Amount;
        action.Hits++;
    }
}

/// <summary>Un combat : du premier coup porté jusqu'à la sortie de combat.</summary>
public sealed class Encounter
{
    private readonly Dictionary<uint, Combatant> combatants = new();
    private readonly Dictionary<string, long> damageByTarget = new();

    public DateTime StartUtc { get; }
    public DateTime LastDamageUtc { get; private set; }
    public DateTime? EndUtc { get; private set; }
    public string ZoneName { get; }
    public long TotalDamage { get; private set; }

    public bool IsActive => EndUtc == null;
    public IEnumerable<Combatant> Combatants => combatants.Values;
    public int CombatantCount => combatants.Count;

    public Encounter(DateTime startUtc, string zoneName)
    {
        StartUtc = startUtc;
        LastDamageUtc = startUtc;
        ZoneName = zoneName;
    }

    /// <summary>La cible qui a encaissé le plus de dégâts donne son nom au combat.</summary>
    public string Title
    {
        get
        {
            var best = damageByTarget.Where(t => t.Key.Length > 0).OrderByDescending(t => t.Value).FirstOrDefault();
            if (!string.IsNullOrEmpty(best.Key))
                return best.Key;
            return string.IsNullOrEmpty(ZoneName) ? "Combat" : ZoneName;
        }
    }

    /// <summary>En cours : jusqu'à maintenant. Terminé : jusqu'au dernier coup, sans le temps d'attente de fin de combat.</summary>
    public double DurationSeconds(DateTime nowUtc)
    {
        var end = IsActive ? nowUtc : LastDamageUtc;
        return Math.Max(1.0, (end - StartUtc).TotalSeconds);
    }

    public void Add(in Hit hit, DateTime nowUtc)
    {
        if (hit.Amount <= 0)
            return;

        if (!combatants.TryGetValue(hit.SourceId, out var combatant))
            combatants[hit.SourceId] = combatant = new Combatant(hit.SourceId, hit.SourceName, hit.JobId, hit.IsLocal);

        // Le joueur a pu changer de job entre deux combats mais pas en plein combat : on garde juste la dernière info connue.
        if (hit.JobId != 0)
            combatant.JobId = hit.JobId;
        combatant.IsLocal |= hit.IsLocal;
        combatant.Add(hit);

        TotalDamage += hit.Amount;
        if (hit.TargetName.Length > 0)
        {
            damageByTarget.TryGetValue(hit.TargetName, out var taken);
            damageByTarget[hit.TargetName] = taken + hit.Amount;
        }

        if (nowUtc > LastDamageUtc)
            LastDamageUtc = nowUtc;
    }

    public void End(DateTime nowUtc) => EndUtc ??= nowUtc;
}
