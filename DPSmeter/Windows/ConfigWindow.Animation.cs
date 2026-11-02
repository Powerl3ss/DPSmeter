using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;

namespace DPSmeter.Windows;

/// <summary>Petites animations de la fenêtre de réglages (glissements, fondus, interrupteurs...).</summary>
public sealed partial class ConfigWindow
{
    private const float PageTransitionSeconds = 0.25f;
    private const float FoldSeconds = 0.22f;

    // Valeurs animées, indexées par identifiant ImGui.
    private readonly Dictionary<uint, float> animated = new();

    // Changement de page
    private Page lastPage = Page.Home;
    private double pageChangedAt = -10;

    // Repli / dépli de la fenêtre
    private bool folding;
    private float foldFrom;
    private float foldTo;
    private double foldStartedAt;

    /// <summary>
    /// Rapproche en douceur une valeur de sa cible (amortissement exponentiel, indépendant des FPS).
    /// La première fois, la valeur part directement de la cible.
    /// </summary>
    private float Animate(uint key, float target, float speed = 16f)
    {
        var dt = ImGui.GetIO().DeltaTime;
        if (!animated.TryGetValue(key, out var value))
            value = target;

        value += (target - value) * (1f - MathF.Exp(-speed * dt));
        if (MathF.Abs(target - value) < 0.0005f)
            value = target;

        animated[key] = value;
        return value;
    }

    /// <summary>Courbe de ralentissement en fin de mouvement.</summary>
    private static float EaseOutCubic(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var inv = 1f - t;
        return 1f - inv * inv * inv;
    }

    /// <summary>Avancement (0 à 1, adouci) de l'apparition de la page courante.</summary>
    private float PageAppear()
    {
        if (page != lastPage)
        {
            lastPage = page;
            pageChangedAt = ImGui.GetTime();
        }

        return EaseOutCubic((float)(ImGui.GetTime() - pageChangedAt) / PageTransitionSeconds);
    }

    private void StartFold(float from, float to)
    {
        folding = true;
        foldFrom = from;
        foldTo = to;
        foldStartedAt = ImGui.GetTime();
        SizeConstraints = FoldingConstraints;
    }

    /// <summary>
    /// À appeler dans PreDraw. Dalamud applique la taille de la fenêtre juste après PreDraw
    /// (à partir de Size et SizeCondition) : on passe donc la hauteur animée par ces propriétés,
    /// sinon elle serait écrasée à chaque image.
    /// </summary>
    private void ApplyFoldAnimation()
    {
        if (resetSizeConditionPending)
        {
            // L'animation est finie : la taille redevient libre (redimensionnable à la souris).
            SizeCondition = ImGuiCond.FirstUseEver;
            resetSizeConditionPending = false;
        }

        if (!folding)
            return;

        var t = (float)(ImGui.GetTime() - foldStartedAt) / FoldSeconds;
        var height = foldFrom + (foldTo - foldFrom) * EaseOutCubic(t);
        Size = new System.Numerics.Vector2(lastSize.X, height) / S;
        SizeCondition = ImGuiCond.Always;

        if (t >= 1f)
        {
            folding = false;
            resetSizeConditionPending = true;
            SizeConstraints = collapsed ? CollapsedConstraints : ExpandedConstraints;
        }
    }

    private bool resetSizeConditionPending;
}
