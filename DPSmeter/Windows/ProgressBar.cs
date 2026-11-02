using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DPSmeter.Windows;

/// <summary>
/// Barre de progression dessinée à la main : piste sombre arrondie, remplissage en dégradé
/// de la couleur d'accent, reflet en haut, halo au bout et reflet lumineux qui la traverse.
/// La partie remplie glisse en douceur vers sa nouvelle valeur.
/// </summary>
public sealed class ProgressBar
{
    private float shownFraction = -1f;

    /// <summary>Dessine la barre à la position du curseur. <paramref name="px"/> : taille d'un pixel à l'échelle de l'interface.</summary>
    public void Draw(float fraction, Vector2 size, Vector4 accent, float px, string? tooltip)
    {
        var width = size.X;
        var height = size.Y;
        var radius = height / 2f;

        // Animation du remplissage (sans animation à la première image).
        var dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);
        shownFraction = shownFraction < 0f
            ? fraction
            : shownFraction + (fraction - shownFraction) * (1f - MathF.Exp(-dt * 7f));
        if (MathF.Abs(shownFraction - fraction) < 0.001f)
            shownFraction = fraction;

        ImGui.Dummy(size);
        var min = ImGui.GetItemRectMin();
        var max = min + size;
        var hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        var alpha = ImGui.GetStyle().Alpha;
        var line = MathF.Max(1f, px);

        uint Col(Vector4 c, float a = 1f) => ImGui.GetColorU32(c with { W = c.W * a * alpha });
        static Vector4 Mix(Vector4 a, Vector4 b, float t) => a + (b - a) * t;
        var white = new Vector4(1f, 1f, 1f, 1f);
        var black = new Vector4(0f, 0f, 0f, 1f);

        // Piste : sombre, légèrement teintée, avec un fin contour.
        dl.AddRectFilled(min, max, Col(Mix(black, accent, 0.18f), 0.70f), radius);
        dl.AddRect(min, max, Col(accent, hovered ? 0.55f : 0.28f), radius, ImDrawFlags.None, line);

        var fillW = width * Math.Clamp(shownFraction, 0f, 1f);
        if (fillW > 0.5f)
        {
            var left = Mix(accent, black, 0.25f);   // début plus profond
            var right = Mix(accent, white, 0.35f);  // bout plus lumineux
            var end = new Vector2(min.X + fillW, max.Y);

            if (fillW <= height)
            {
                // Trop court pour un dégradé : une simple pastille arrondie.
                dl.AddRectFilled(min, end, Col(Mix(left, right, 0.5f)), MathF.Min(radius, fillW / 2f));
            }
            else
            {
                // Dégradé horizontal avec des bouts arrondis : deux disques et un rectangle en dégradé.
                dl.AddCircleFilled(new Vector2(min.X + radius, min.Y + radius), radius, Col(left), 24);
                dl.AddCircleFilled(new Vector2(end.X - radius, min.Y + radius), radius, Col(right), 24);
                dl.AddRectFilledMultiColor(new Vector2(min.X + radius, min.Y), new Vector2(end.X - radius, max.Y),
                    Col(left), Col(right), Col(right), Col(left));

                // Reflet en haut, pour un léger effet de relief.
                var gloss = MathF.Max(1f, MathF.Round(height * 0.28f));
                dl.AddRectFilled(new Vector2(min.X + radius, min.Y + 1f), new Vector2(end.X - radius, min.Y + 1f + gloss),
                    Col(white, 0.22f), gloss / 2f);

                // Reflet lumineux qui traverse la partie remplie toutes les quelques secondes.
                var t = (float)(ImGui.GetTime() % 3.2) / 3.2f;
                if (t < 0.45f)
                {
                    var band = height * 4f;
                    var x = min.X - band + (fillW + band * 2f) * (t / 0.45f);
                    dl.PushClipRect(new Vector2(min.X + radius * 0.5f, min.Y), new Vector2(end.X - radius * 0.5f, max.Y), true);
                    var clear = Col(white, 0f);
                    var shine = Col(white, 0.35f);
                    dl.AddRectFilledMultiColor(new Vector2(x - band / 2f, min.Y), new Vector2(x, max.Y), clear, shine, shine, clear);
                    dl.AddRectFilledMultiColor(new Vector2(x, min.Y), new Vector2(x + band / 2f, max.Y), shine, clear, clear, shine);
                    dl.PopClipRect();
                }
            }

            // Halo au bout de la partie remplie, et contour lumineux une fois terminée.
            var glowCenter = new Vector2(end.X - radius, min.Y + radius);
            for (var i = 3; i >= 1; i--)
                dl.AddCircleFilled(glowCenter, radius * (1f + i * 0.45f), Col(right, 0.07f), 24);
            if (shownFraction >= 0.999f)
                dl.AddRect(min - new Vector2(line), max + new Vector2(line), Col(right, 0.35f), radius + line, ImDrawFlags.None, line);
        }

        if (hovered && !string.IsNullOrEmpty(tooltip))
            ImGui.SetTooltip(tooltip);
    }
}
