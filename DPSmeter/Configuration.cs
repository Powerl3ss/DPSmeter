using System;
using System.Numerics;
using Dalamud.Configuration;

namespace DPSmeter;

/// <summary>Qui apparaît dans le tableau.</summary>
public enum MeterScope
{
    Self,
    Party,
    Alliance,
    AllPlayers,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // --- HUD ---
    public bool HudVisible { get; set; } = true;
    public bool HudLocked { get; set; } = false;
    public bool ClickThrough { get; set; } = false;   // n'agit que si le HUD est verrouillé
    public float HudOpacity { get; set; } = 0.65f;
    public float HudScale { get; set; } = 1.0f;
    public float HudWidth { get; set; } = 300f;
    public bool CompactMode { get; set; } = false;
    public bool HudCollapsed { get; set; } = false;

    // --- Contenu du HUD ---
    public int MaxRows { get; set; } = 8;
    public bool ShowJobIcons { get; set; } = true;
    public bool ShowPercent { get; set; } = true;
    public bool ShowTotalDamage { get; set; } = false;
    public bool HideOtherNames { get; set; } = false;

    // --- Masquage automatique ---
    public bool HideOutOfCombat { get; set; } = false;
    public int HideDelaySeconds { get; set; } = 30;
    public bool OnlyInDuty { get; set; } = false;

    // --- Mesure ---
    public MeterScope Scope { get; set; } = MeterScope.Party;
    public bool MergePets { get; set; } = true;
    public bool SeparateLimitBreak { get; set; } = true;
    public int EndDelaySeconds { get; set; } = 5;
    public int MaxHistory { get; set; } = 20;
    public bool ChatSummary { get; set; } = false;

    // --- Couleurs ---
    public bool UseJobColors { get; set; } = true;
    public float BarOpacity { get; set; } = 0.70f;
    public Vector4 AccentColor { get; set; } = new(0.30f, 0.65f, 1.00f, 1.00f);
    public Vector4 SecondaryColor { get; set; } = new(0.80f, 0.84f, 0.90f, 1.00f);

    // --- Lisibilité ---
    public bool TextOutline { get; set; } = true;
    public float OutlineStrength { get; set; } = 0.90f;

    // --- Fenêtre de réglages ---
    public bool AnimatedSettingsBackground { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
