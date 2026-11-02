using System;
using System.Collections.Generic;

namespace DPSmeter.Core;

/// <summary>
/// Décide quand un combat commence et se termine, et garde les combats passés.
/// Les coups arrivent par les hooks du jeu et l'affichage lit depuis ImGui : tout passe par un verrou.
/// </summary>
public sealed class EncounterTracker
{
    private readonly object gate = new();
    private readonly List<Encounter> history = new();
    private Encounter? current;

    /// <summary>Nombre de combats gardés en mémoire.</summary>
    public int MaxHistory { get; set; } = 20;

    /// <summary>Prévient quand un combat vient de se terminer (message dans le chat).</summary>
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
                return current ?? (history.Count > 0 ? history[0] : null);
        }
    }

    /// <summary>Combats terminés, du plus récent au plus ancien.</summary>
    public Encounter[] History
    {
        get
        {
            lock (gate)
                return history.ToArray();
        }
    }

    /// <summary>
    /// Compte un coup. Un coup direct démarre un combat si besoin. Un tick de DoT n'en démarre jamais :
    /// sinon les derniers ticks après la mort d'une cible ouvriraient un faux combat.
    /// </summary>
    public bool Record(in Hit hit, DateTime nowUtc, string zoneName)
    {
        lock (gate)
        {
            if (hit.Amount <= 0)
                return false;

            if (current == null)
            {
                if (hit.IsDot)
                    return false;
                current = new Encounter(nowUtc, zoneName);
            }

            current.Add(hit, nowUtc);
            return true;
        }
    }

    /// <summary>
    /// À appeler régulièrement. Le combat se termine quand le joueur n'est plus en combat et que plus
    /// personne ne frappe depuis <paramref name="endDelaySeconds"/> secondes. Tant que le jeu indique
    /// « en combat », il continue : un boss peut devenir inciblable un long moment.
    /// </summary>
    public void Update(DateTime nowUtc, bool inCombat, double endDelaySeconds)
    {
        Encounter? ended = null;
        lock (gate)
        {
            if (current != null && !inCombat && (nowUtc - current.LastDamageUtc).TotalSeconds >= endDelaySeconds)
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

    /// <summary>Oublie le combat en cours et tout l'historique.</summary>
    public void Clear()
    {
        lock (gate)
        {
            current = null;
            history.Clear();
        }
    }

    /// <summary>Lit le combat en cours sans risquer d'écrire en même temps.</summary>
    public EncounterView? BuildView(Encounter? encounter, DateTime nowUtc)
    {
        lock (gate)
            return encounter == null ? null : EncounterView.From(encounter, nowUtc);
    }

    private Encounter Archive(DateTime nowUtc)
    {
        var ended = current!;
        current = null;
        ended.End(nowUtc);

        history.Insert(0, ended);
        var max = Math.Max(1, MaxHistory);
        if (history.Count > max)
            history.RemoveRange(max, history.Count - max);
        return ended;
    }
}
