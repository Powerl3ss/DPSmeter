using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DPSmeter.Windows;

/// <summary>
/// Fond animé de la fenêtre de réglages : petits halos lumineux qui dérivent et particules qui scintillent.
/// L'effet est calculé par bandes de la hauteur de l'en-tête, empilées sur toute la fenêtre :
/// repliée ou dépliée, la fenêtre a donc exactement le même rendu.
/// </summary>
public sealed partial class ConfigWindow
{
    // Halos d'une bande : vitesse X, vitesse Y, décalage X, décalage Y, rayon (part de la hauteur de bande), intensité.
    private static readonly (float SpeedX, float SpeedY, float PhaseX, float PhaseY, float Radius, float Alpha)[] Glows =
    {
        (0.050f, 0.070f, 0.0f, 1.3f, 0.55f, 0.11f),
        (0.035f, 0.045f, 2.1f, 0.4f, 0.45f, 0.08f),
        (0.060f, 0.030f, 4.0f, 2.7f, 0.38f, 0.07f),
    };

    private const int ParticlesPerBand = 26;

    private void DrawAnimatedBackground(Vector2 origin, Vector2 size)
    {
        if (!Cfg.AnimatedSettingsBackground)
            return;

        var time = (float)ImGui.GetTime();
        var drawList = ImGui.GetWindowDrawList();
        var bandH = HeaderHeight * S;
        var bands = Math.Max(1, (int)MathF.Ceiling(size.Y / bandH));

        for (var band = 0; band < bands; band++)
            DrawBand(drawList, origin + new Vector2(0, band * bandH), new Vector2(size.X, bandH), band, time);
    }

    private void DrawBand(ImDrawListPtr drawList, Vector2 origin, Vector2 size, int band, float time)
    {
        // Chaque bande a ses propres décalages, pour que le motif ne se répète pas d'une bande à l'autre.
        var bandPhase = Hash(band * 7 + 11) * MathF.Tau;

        foreach (var glow in Glows)
        {
            var center = origin + new Vector2(
                size.X * (0.5f + 0.36f * MathF.Sin(time * glow.SpeedX * MathF.Tau + glow.PhaseX + bandPhase)),
                size.Y * (0.5f + 0.32f * MathF.Cos(time * glow.SpeedY * MathF.Tau + glow.PhaseY + bandPhase * 0.7f)));
            SoftCircle(drawList, center, size.Y * glow.Radius, Accent, glow.Alpha);
        }

        for (var i = 0; i < ParticlesPerBand; i++)
        {
            var n = band * 1000 + i;
            var seedX = Hash(n * 3 + 1);
            var seedSpeed = Hash(n * 3 + 2);
            var seedPhase = Hash(n * 3 + 3);

            var speed = 0.012f + seedSpeed * 0.025f;
            var progress = Frac(seedPhase + time * speed);
            var x = origin.X + size.X * Frac(seedX + 0.03f * MathF.Sin(time * 0.3f + i));
            var y = origin.Y + size.Y * (1.05f - progress * 1.1f);
            var twinkle = 0.5f + 0.5f * MathF.Sin(time * (1.2f + seedSpeed * 2f) + i);
            var fade = MathF.Sin(progress * MathF.PI);
            var radius = (1.0f + seedSpeed * 1.6f) * S;

            drawList.AddCircleFilled(new Vector2(x, y), radius, Col(Accent with { W = 0.10f + 0.30f * twinkle * fade }));
        }
    }

    private static void SoftCircle(ImDrawListPtr drawList, Vector2 center, float radius, Vector4 color, float alpha)
    {
        const int layers = 12;
        for (var i = layers; i >= 1; i--)
        {
            var f = i / (float)layers;
            drawList.AddCircleFilled(center, radius * f, Col(color with { W = alpha * 1.8f / layers }), 32);
        }
    }

    private static float Hash(int n) => Frac(MathF.Sin(n * 12.9898f) * 43758.5453f);

    private static float Frac(float v) => v - MathF.Floor(v);
}
