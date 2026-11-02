using System;
using System.Collections.Generic;

namespace DPSmeter.Core;

/// <summary>
/// Décide quand un combat commence et se termine, et garde les derniers combats terminés.
/// Les événements arrivent par les hooks du jeu et l'affichage lit depuis ImGui : tout passe par un verrou.
/// </summary>
public sealed class EncounterTracker
{
    private readonly object gate = new();
    private readonly List<Encounter> history = new();
    private Encounter? current;
    private bool showLastEnded = true;

    /// <summary>Nombre de combats terminés gardés en mémoire (ceux enregistrés sont dans EncounterStore).</summary>
    public int MaxHistory { get; set; } = 20;

    /// <summary>Fenêtre du DPS récent, en secondes.</summary>
    public int RecentWindowSeconds { get; set; } = 10;

    /// <summary>Prévient quand un combat vient de se terminer.</summary>
    public event Action<Encounter>? EncounterEnded;

    public bool IsActive
    {
        get
        {
            lock (gate)
                return current != null;
        }
    }

    /// <summary>Le combat en cours, sinon le dernier terminé (qui reste affiché jusqu'au suivant).</summary>
    public Encounter? Displayed
    {
        get
        {
            lock (gate)
                return current ?? (showLastEnded && history.Count > 0 ? history[0] : null);
        }
    }

    /// <summary>Combats terminés de la session, du plus récent au plus ancien.</summary>
    public Encounter[] History
    {
        get
        {
            lock (gate)
                return history.ToArray();
        }
    }

    /// <summary>
    /// Compte des dégâts infligés. Un coup direct démarre un combat si besoin. Un tick de DoT n'en démarre
    /// jamais : sinon les derniers ticks après la mort d'une cible ouvriraient un faux combat.
    /// </summary>
    public bool Record(in Hit hit, DateTime nowUtc, string zoneName, bool dutyWide = false)
    {
        lock (gate)
        {
            if (hit.Amount <= 0)
                return false;

            if (current == null)
            {
                if (hit.IsDot)
                    return false;
                current = new Encounter(nowUtc, zoneName, dutyWide);
            }

            current.Add(hit, nowUtc);
            return true;
        }
    }

    /// <summary>Compte un soin. Ne démarre jamais de combat : soigner hors combat n'en est pas un.</summary>
    public void RecordHealing(in Hit hit)
    {
        lock (gate)
            current?.AddHealing(hit);
    }

    /// <summary>Compte des dégâts subis. Ne démarre jamais de combat.</summary>
    public void RecordDamageTaken(in Hit hit)
    {
        lock (gate)
            current?.AddDamageTaken(hit);
    }

    /// <summary>Compte la mort d'un membre du tableau, pendant un combat.</summary>
    public void RecordDeath(in Hit member, DateTime nowUtc)
    {
        lock (gate)
            current?.AddDeath(member, nowUtc);
    }

    public void RecordTargetDeath(uint targetId)
    {
        lock (gate)
            current?.MarkTargetDead(targetId);
    }

    public void UpdateTargetHp(uint targetId, float hp)
    {
        lock (gate)
            current?.UpdateTargetHp(targetId, hp);
    }

    /// <summary>Le combat en cours couvre-t-il toute une instance ?</summary>
    public bool IsCurrentDutyWide
    {
        get
        {
            lock (gate)
                return current is { IsDutyWide: true };
        }
    }

    /// <summary>Dernière cible touchée dans le combat en cours (0 s'il n'y en a pas).</summary>
    public uint LastTargetId
    {
        get
        {
            lock (gate)
                return current?.LastTargetId ?? 0;
        }
    }

    /// <summary>Remet l'affichage à zéro sans rien oublier : le dernier combat n'est plus montré, jusqu'au suivant.</summary>
    public void HideLastEnded()
    {
        lock (gate)
            showLastEnded = false;
    }

    /// <summary>Cible principale du combat en cours (0 s'il n'y en a pas).</summary>
    public uint MainTargetId
    {
        get
        {
            lock (gate)
                return current?.MainTargetId ?? 0;
        }
    }

    /// <summary>Note l'issue annoncée par le jeu. Renvoie faux si aucun combat n'est en cours.</summary>
    public bool MarkOutcome(Outcome outcome)
    {
        lock (gate)
        {
            if (current == null)
                return false;
            current.MarkOutcome(outcome);
            return true;
        }
    }

    /// <summary>Note l'issue sur le dernier combat, s'il vient de se terminer. Renvoie ce combat, ou null.</summary>
    public Encounter? MarkLastEnded(Outcome outcome, DateTime nowUtc, double graceSeconds)
    {
        lock (gate)
        {
            if (history.Count == 0 || history[0].EndUtc is not { } end || (nowUtc - end).TotalSeconds > graceSeconds)
                return null;
            history[0].MarkOutcome(outcome);
            return history[0];
        }
    }

    /// <summary>
    /// À appeler régulièrement. Le combat se termine quand le joueur n'est plus en combat et que plus
    /// personne ne frappe depuis <paramref name="endDelaySeconds"/> secondes. Tant que le jeu indique
    /// « en combat », il continue : un boss peut devenir inciblable un long moment. Un combat couvrant
    /// une instance ne s'arrête jamais ici, seulement sur un wipe, la fin de l'instance ou sa sortie.
    /// </summary>
    public void Update(DateTime nowUtc, bool inCombat, double endDelaySeconds)
    {
        Encounter? ended = null;
        lock (gate)
        {
            if (current is { IsDutyWide: false } && !inCombat && (nowUtc - current.LastDamageUtc).TotalSeconds >= endDelaySeconds)
                ended = Archive(nowUtc);
        }

        if (ended != null)
            EncounterEnded?.Invoke(ended);
    }

    /// <summary>Termine tout de suite le combat en cours (changement de zone, commande).</summary>
    public void EndCurrent(DateTime nowUtc)
    {
        Encounter? ended;
        lock (gate)
            ended = current != null ? Archive(nowUtc) : null;

        if (ended != null)
            EncounterEnded?.Invoke(ended);
    }

    /// <summary>Oublie le combat en cours et les combats terminés gardés en mémoire.</summary>
    public void Clear()
    {
        lock (gate)
        {
            current = null;
            history.Clear();
            showLastEnded = true;
        }
    }

    /// <summary>Lit un combat sans risquer d'écrire en même temps.</summary>
    public EncounterView? BuildView(Encounter? encounter, DateTime nowUtc)
    {
        lock (gate)
            return encounter == null ? null : EncounterView.From(encounter, nowUtc, RecentWindowSeconds);
    }

    private Encounter Archive(DateTime nowUtc)
    {
        var ended = current!;
        current = null;
        ended.End(nowUtc);

        showLastEnded = true;
        history.Insert(0, ended);
        var max = Math.Max(1, MaxHistory);
        if (history.Count > max)
            history.RemoveRange(max, history.Count - max);
        return ended;
    }
}
