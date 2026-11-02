using System;
using System.Collections.Generic;
using System.Linq;

namespace DPSmeter.Core;

/// <summary>Comment un combat s'est terminé. Les combats enregistrés avant la v0.3 restent à Unknown.</summary>
public enum Outcome
{
    Unknown = 0,
    Victory = 1,
    Failure = 2,
    Interrupted = 3,
}

/// <summary>Un événement à compter : déjà attribué à une ligne du tableau.</summary>
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
    bool IsEstimated = false,
    uint TargetId = 0,
    float TargetHp = -1f);

/// <summary>Total d'une action pour un combattant.</summary>
public sealed class ActionTotal
{
    public string Name { get; }
    public long Amount { get; set; }
    public int Hits { get; set; }
    public long MaxHit { get; set; }

    public ActionTotal(string name) => Name = name;

    public void Add(long amount)
    {
        Amount += amount;
        Hits++;
        if (amount > MaxHit)
            MaxHit = amount;
    }
}

/// <summary>Tout ce qu'un joueur (ou une ligne spéciale) a fait pendant un combat.</summary>
public sealed class Combatant
{
    /// <summary>Plus longue fenêtre possible pour le DPS récent, en secondes.</summary>
    public const int MaxRecentWindow = 30;

    private readonly Dictionary<string, ActionTotal> actions = new();
    private readonly Dictionary<string, ActionTotal> healActions = new();
    private readonly List<long> timeline = new();
    private readonly List<double> deathSeconds = new();

    // Dégâts de chaque seconde récente : case = seconde modulo la taille, marquée par sa seconde exacte.
    private readonly long[] recentDamage = new long[MaxRecentWindow + 1];
    private readonly long[] recentSecond = new long[MaxRecentWindow + 1];

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
    public long Healing { get; private set; }
    public long DamageTaken { get; private set; }

    public IEnumerable<ActionTotal> Actions => actions.Values;
    public IEnumerable<ActionTotal> HealActions => healActions.Values;
    public IReadOnlyList<long> Timeline => timeline;
    public IReadOnlyList<double> DeathSeconds => deathSeconds;

    public Combatant(uint id, string name, uint jobId, bool isLocal)
    {
        Id = id;
        Name = name;
        JobId = jobId;
        IsLocal = isLocal;
        Array.Fill(recentSecond, -1L);
    }

    public void AddDamage(in Hit hit, double elapsedSeconds)
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

        AddTo(actions, hit);
        AddToTimeline(hit.Amount, elapsedSeconds);
        AddToRecent(hit.Amount, elapsedSeconds);
    }

    public void AddHealing(in Hit hit)
    {
        Healing += hit.Amount;
        AddTo(healActions, hit);
    }

    public void AddDamageTaken(long amount) => DamageTaken += amount;

    public void AddDeath(double elapsedSeconds) => deathSeconds.Add(elapsedSeconds);

    /// <summary>DPS des dernières secondes écoulées, sans compter la seconde en cours (encore incomplète).</summary>
    public double RecentDps(double elapsedSeconds, int windowSeconds)
    {
        var window = Math.Clamp(windowSeconds, 1, MaxRecentWindow);
        var now = (long)Math.Floor(Math.Max(0, elapsedSeconds));
        var sum = 0L;
        for (var second = now - window; second < now; second++)
        {
            if (second < 0)
                continue;
            var slot = (int)(second % recentDamage.Length);
            if (recentSecond[slot] == second)
                sum += recentDamage[slot];
        }

        // Au début du combat, on divise par le temps réellement écoulé.
        return sum / (double)Math.Max(1, Math.Min(window, now));
    }

    private static void AddTo(Dictionary<string, ActionTotal> totals, in Hit hit)
    {
        if (!totals.TryGetValue(hit.ActionName, out var action))
            totals[hit.ActionName] = action = new ActionTotal(hit.ActionName);
        action.Add(hit.Amount);
    }

    private void AddToTimeline(long amount, double elapsedSeconds)
    {
        var index = (int)(Math.Max(0, elapsedSeconds) / Encounter.TimelineStepSeconds);
        while (timeline.Count <= index)
            timeline.Add(0);
        timeline[index] += amount;
    }

    private void AddToRecent(long amount, double elapsedSeconds)
    {
        var second = (long)Math.Floor(Math.Max(0, elapsedSeconds));
        var slot = (int)(second % recentDamage.Length);
        if (recentSecond[slot] != second)
        {
            recentSecond[slot] = second;
            recentDamage[slot] = 0;
        }
        recentDamage[slot] += amount;
    }
}

/// <summary>Un combat : du premier coup porté jusqu'à la sortie de combat.</summary>
public sealed class Encounter
{
    /// <summary>Largeur d'un point de la courbe de DPS.</summary>
    public const int TimelineStepSeconds = 5;

    // PV au-dessus desquels une cible n'a pas vraiment été entamée (mannequin, combat abandonné).
    private const float UntouchedHp = 0.995f;

    private sealed class TargetState
    {
        public long Damage;
        public float LastHp = -1f;
        public bool IsDead;
    }

    private readonly Dictionary<uint, Combatant> combatants = new();
    private readonly Dictionary<string, long> damageByTarget = new();
    private readonly Dictionary<uint, TargetState> targets = new();
    private Outcome markedOutcome;

    public DateTime StartUtc { get; }
    public DateTime LastDamageUtc { get; private set; }
    public DateTime? EndUtc { get; private set; }
    public string ZoneName { get; }
    public long TotalDamage { get; private set; }
    public long TotalHealing { get; private set; }
    public long TotalDamageTaken { get; private set; }

    public Outcome Outcome { get; private set; }

    /// <summary>PV restants de la cible principale après un échec (0 à 1), sinon négatif.</summary>
    public float RemainingHp { get; private set; } = -1f;

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

    /// <summary>La cible qui a encaissé le plus de dégâts (0 si aucune n'est connue).</summary>
    public uint MainTargetId
    {
        get
        {
            uint best = 0;
            var bestDamage = -1L;
            foreach (var (id, target) in targets)
            {
                if (target.Damage > bestDamage)
                {
                    best = id;
                    bestDamage = target.Damage;
                }
            }
            return best;
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

        GetCombatant(hit).AddDamage(hit, Elapsed(nowUtc));

        TotalDamage += hit.Amount;
        if (hit.TargetName.Length > 0)
        {
            damageByTarget.TryGetValue(hit.TargetName, out var taken);
            damageByTarget[hit.TargetName] = taken + hit.Amount;
        }

        if (hit.TargetId != 0)
        {
            if (!targets.TryGetValue(hit.TargetId, out var target))
                targets[hit.TargetId] = target = new TargetState();
            target.Damage += hit.Amount;
            if (hit.TargetHp >= 0f)
                target.LastHp = hit.TargetHp;
        }

        if (nowUtc > LastDamageUtc)
            LastDamageUtc = nowUtc;
    }

    public void AddHealing(in Hit hit)
    {
        if (hit.Amount <= 0)
            return;
        GetCombatant(hit).AddHealing(hit);
        TotalHealing += hit.Amount;
    }

    public void AddDamageTaken(in Hit hit)
    {
        if (hit.Amount <= 0)
            return;
        GetCombatant(hit).AddDamageTaken(hit.Amount);
        TotalDamageTaken += hit.Amount;
    }

    public void AddDeath(in Hit hit, DateTime nowUtc) => GetCombatant(hit).AddDeath(Elapsed(nowUtc));

    public void MarkTargetDead(uint targetId)
    {
        if (targets.TryGetValue(targetId, out var target))
            target.IsDead = true;
    }

    public void UpdateTargetHp(uint targetId, float hp)
    {
        if (hp >= 0f && targets.TryGetValue(targetId, out var target))
            target.LastHp = hp;
    }

    /// <summary>Le jeu annonce lui-même l'issue (instance terminée, équipe vaincue) : elle prime sur tout le reste.</summary>
    public void MarkOutcome(Outcome outcome)
    {
        markedOutcome = outcome;

        // L'annonce peut arriver quelques secondes après la fin du combat.
        if (EndUtc != null)
            ResolveOutcome();
    }

    public void End(DateTime nowUtc)
    {
        if (EndUtc != null)
            return;

        EndUtc = nowUtc;
        ResolveOutcome();
    }

    private void ResolveOutcome()
    {
        targets.TryGetValue(MainTargetId, out var main);
        RemainingHp = -1f;

        if (markedOutcome != Outcome.Unknown)
        {
            Outcome = markedOutcome;
            RemainingHp = Outcome == Outcome.Failure && main is { LastHp: >= 0f } ? main.LastHp : -1f;
            return;
        }

        if (main == null)
        {
            Outcome = Outcome.Unknown;
            return;
        }

        if (main.IsDead || main.LastHp == 0f)
        {
            Outcome = Outcome.Victory;
        }
        else if (main.LastHp < 0f)
        {
            Outcome = Outcome.Unknown;
        }
        else if (main.LastHp >= UntouchedHp)
        {
            Outcome = Outcome.Interrupted;
        }
        else
        {
            Outcome = Outcome.Failure;
            RemainingHp = main.LastHp;
        }
    }

    private double Elapsed(DateTime nowUtc) => Math.Max(0, (nowUtc - StartUtc).TotalSeconds);

    private Combatant GetCombatant(in Hit hit)
    {
        if (!combatants.TryGetValue(hit.SourceId, out var combatant))
            combatants[hit.SourceId] = combatant = new Combatant(hit.SourceId, hit.SourceName, hit.JobId, hit.IsLocal);

        if (hit.JobId != 0)
            combatant.JobId = hit.JobId;
        combatant.IsLocal |= hit.IsLocal;
        return combatant;
    }
}
