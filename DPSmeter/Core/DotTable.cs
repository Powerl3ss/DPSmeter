using System;
using System.Collections.Generic;

namespace DPSmeter.Core;

/// <summary>Part d'un tick de DoT revenant à un joueur.</summary>
public readonly record struct DotShare(uint SourceId, uint StatusId, float Weight, uint Amount = 0);

/// <summary>
/// DoT des jobs jouables. Le jeu donne le montant d'un tick, pas toujours qui l'a causé : quand
/// plusieurs joueurs sont possibles, le tick est partagé selon la puissance de leurs DoT.
/// Puissances reprises des définitions publiques du plugin ACT (ravahn/FFXIV_ACT_Plugin). Elles servent
/// seulement de poids : un écart après un patch déplace un peu de dégâts entre joueurs, pas le total.
/// </summary>
public static class DotTable
{
    private static readonly Dictionary<uint, float> Potency = new()
    {
        // Astromancien
        [838] = 50, [843] = 60, [1881] = 70,
        // Barde
        [124] = 15, [129] = 20, [1200] = 20, [1201] = 25,
        // Mage noir
        [161] = 45, [162] = 30, [163] = 50, [1210] = 35, [3871] = 60, [3872] = 40,
        // Chevalier dragon
        [118] = 40, [2719] = 45,
        // Pistosabreur
        [1837] = 120, [1838] = 60,
        // Machiniste
        [1866] = 50,
        // Paladin
        [248] = 30,
        // Sage
        [2614] = 40, [2615] = 60, [2616] = 85, [3897] = 40,
        // Samouraï
        [1228] = 50,
        // Érudit
        [179] = 20, [189] = 40, [1895] = 85, [3883] = 140,
        // Mage blanc
        [143] = 30, [144] = 50, [1871] = 85,
    };

    private static readonly Dictionary<uint, float> Ground = new()
    {
        [749] = 50,   // Terre saline (chevalier noir)
        [1205] = 120, // Lance-flammes (machiniste)
        [501] = 80,   // Doton (ninja)
        [2706] = 30,  // Courant de feu (invocateur)
    };

    public static bool IsKnownDot(uint statusId) => Potency.ContainsKey(statusId) || Ground.ContainsKey(statusId);

    public static bool IsGround(uint statusId) => Ground.ContainsKey(statusId);

    public static float WeightOf(uint statusId) =>
        Potency.TryGetValue(statusId, out var potency) ? potency
        : Ground.TryGetValue(statusId, out var ground) ? ground
        : 1f;

    /// <summary>
    /// Partage un tick selon les poids. Le reste de l'arrondi va aux plus grosses parts :
    /// la somme des parts vaut toujours exactement le montant du tick.
    /// </summary>
    public static List<DotShare> Split(uint amount, IReadOnlyList<DotShare> candidates)
    {
        var result = new List<DotShare>(candidates.Count);
        if (candidates.Count == 0 || amount == 0)
            return result;

        var totalWeight = 0.0;
        foreach (var c in candidates)
            totalWeight += Math.Max(0f, c.Weight);

        // Aucun poids utilisable : à parts égales.
        var equal = totalWeight <= 0.0;
        if (equal)
            totalWeight = candidates.Count;

        var remainders = new (double Remainder, int Index)[candidates.Count];
        var given = 0L;
        for (var i = 0; i < candidates.Count; i++)
        {
            var weight = equal ? 1.0 : Math.Max(0f, candidates[i].Weight);
            var exact = amount * weight / totalWeight;
            var part = (uint)Math.Floor(exact);
            given += part;
            remainders[i] = (exact - part, i);
            result.Add(candidates[i] with { Amount = part });
        }

        Array.Sort(remainders, (a, b) => b.Remainder.CompareTo(a.Remainder));
        for (var i = 0; given < amount; i = (i + 1) % remainders.Length)
        {
            var index = remainders[i].Index;
            result[index] = result[index] with { Amount = result[index].Amount + 1 };
            given++;
        }

        return result;
    }
}
