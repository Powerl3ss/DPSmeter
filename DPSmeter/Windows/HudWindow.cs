using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using DPSmeter.Core;

namespace DPSmeter.Windows;

/// <summary>Le HUD affiché en jeu : une barre par joueur, du plus gros DPS au plus petit.</summary>
public sealed class HudWindow : Window, IDisposable
{
    private const ImGuiWindowFlags BaseFlags =
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
        ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing |
        ImGuiWindowFlags.NoNav;

    // Icônes de jobs du jeu : 62000 + identifiant du job.
    private const uint JobIconBase = 62000;
    private const float TitleScale = 1.2f;

    private readonly Plugin plugin;
    private Configuration Cfg => plugin.Configuration;

    // Largeur affichée de chaque barre : elle glisse vers sa nouvelle valeur.
    private readonly Dictionary<uint, float> shownFractions = new();

    // Police du titre construite à sa vraie taille : agrandir le texte avec SetWindowFontScale le rend flou.
    private IFontHandle? titleFont;
    private float titleFontPx;

    public HudWindow(Plugin plugin) : base("DPSmeter###DPSmeterHud", BaseFlags)
    {
        this.plugin = plugin;
        RespectCloseHotkey = false; // Échap ne doit pas fermer le HUD
    }

    public void Dispose() => titleFont?.Dispose();

    private IFontHandle? TitleFont(float textScale)
    {
        var basePx = ImGui.GetFont().FontSize / Math.Max(ImGuiHelpers.GlobalScale, 0.01f);
        var px = MathF.Round(basePx * textScale * TitleScale);
        if (titleFont == null || Math.Abs(px - titleFontPx) > 0.1f)
        {
            titleFont?.Dispose();
            titleFontPx = px;
            titleFont = Plugin.PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(
                e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(px)));
        }
        return titleFont.Available ? titleFont : null;
    }

    /// <summary>Décide si le HUD doit être dessiné cette frame.</summary>
    public override bool DrawConditions()
    {
        if (plugin.PreviewMode)
            return true;

        if (Plugin.CurrentCharacterId == 0)
            return false;

        if (Cfg.OnlyInDuty && !Plugin.Condition[ConditionFlag.BoundByDuty])
            return false;

        return !Cfg.HideOutOfCombat || plugin.RecentlyInCombat;
    }

    public override void PreDraw()
    {
        Flags = BaseFlags;
        if (Cfg.HudLocked)
        {
            Flags |= ImGuiWindowFlags.NoMove;
            if (Cfg.ClickThrough)
                Flags |= ImGuiWindowFlags.NoInputs;
        }

        ImGui.SetNextWindowBgAlpha(Cfg.HudOpacity);
    }

    public override void Draw()
    {
        var textScale = Math.Clamp(Cfg.HudScale, 0.6f, 2f);
        ImGui.SetWindowFontScale(textScale);

        var width = Math.Clamp(Cfg.HudWidth, 160f, 700f) * ImGuiHelpers.GlobalScale * textScale;
        var view = plugin.View;

        DrawHeader(view, width, textScale);

        if (!Cfg.HudCollapsed)
        {
            if (view == null || view.Combatants.Count == 0)
            {
                Secondary("En attente d'un combat");
                ImGui.Dummy(new Vector2(width, 0));
            }
            else
            {
                DrawRows(view, width);
            }
        }

        // La largeur du HUD reste fixe, même replié.
        ImGui.Dummy(new Vector2(width, 0));
        DrawContextMenu();
    }

    // ------------------------------------------------------------ En-tête

    private void DrawHeader(EncounterView? view, float width, float textScale)
    {
        var start = ImGui.GetCursorPos();

        // Le titre est un peu plus grand que le reste : les chiffres sont centrés sur lui.
        var font = TitleFont(textScale);
        using (ImRaii.PushColor(ImGuiCol.Text, Cfg.AccentColor))
        {
            if (font != null)
            {
                ImGui.SetWindowFontScale(1f);
                using (font.Push())
                    Text("DPSmeter");
            }
            else
            {
                // Le temps que la police se construise (une ou deux images).
                ImGui.SetWindowFontScale(textScale * TitleScale);
                Text("DPSmeter");
            }
        }
        ImGui.SetWindowFontScale(textScale);
        var titleHeight = ImGui.GetItemRectSize().Y;
        var titleRight = ImGui.GetItemRectMax().X;

        // Un clic sur le titre replie / déplie aussi le HUD.
        if (ImGui.IsItemClicked())
            Toggle(c => c.HudCollapsed = !c.HudCollapsed);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip((Cfg.HudCollapsed ? "Clic pour déplier le HUD" : "Clic pour replier le HUD") + "\nClic droit : réglages et options");

        // Durée et DPS toujours visibles : à zéro tant qu'aucun combat n'a eu lieu.
        var summary = view != null
            ? $"{Formatting.Duration(view.DurationSeconds)}  |  {Formatting.Compact(view.TotalDps)}"
            : $"{Formatting.Duration(0)}  |  {Formatting.Compact(0)}";
        var summaryWidth = ImGui.CalcTextSize(summary).X;
        var y = start.Y + (titleHeight - ImGui.GetTextLineHeight()) / 2f;
        ImGui.SetCursorPos(new Vector2(start.X + width - summaryWidth, y));
        Secondary(summary);
        if (ImGui.IsItemHovered())
        {
            if (view != null)
            {
                var state = view.IsActive ? "En cours" : "Terminé";
                ImGui.SetTooltip($"{view.Title}\n{state}  |  {Formatting.Duration(view.DurationSeconds)}\n{Formatting.Full(view.TotalDps)} DPS du groupe\n{Formatting.Full(view.TotalDamage)} dégâts");
            }
            else
            {
                ImGui.SetTooltip("En attente d'un combat");
            }
        }

        if (view != null)
        {
            // Pastille « en combat » juste après le titre.
            if (view.IsActive)
            {
                var radius = MathF.Round(3f * ImGuiHelpers.GlobalScale * textScale);
                var pulse = 0.55f + 0.45f * MathF.Sin((float)ImGui.GetTime() * 5f);
                var origin = ImGui.GetWindowPos() - new Vector2(ImGui.GetScrollX(), ImGui.GetScrollY());
                var center = new Vector2(titleRight + radius * 2.5f, origin.Y + start.Y + titleHeight / 2f);
                ImGui.GetWindowDrawList().AddCircleFilled(center, radius, ImGui.GetColorU32(Cfg.AccentColor with { W = pulse }));
            }
        }

        ImGui.SetCursorPos(new Vector2(start.X, start.Y + titleHeight + ImGui.GetStyle().ItemSpacing.Y * 0.5f));

        // Fine barre entre le titre et les résultats (rien à séparer quand le HUD est replié).
        if (Cfg.HudCollapsed)
        {
            ImGui.Dummy(Vector2.Zero);
            return;
        }

        var thickness = MathF.Max(1f, MathF.Round(ImGuiHelpers.GlobalScale * textScale));
        var lineMin = ImGui.GetCursorScreenPos();
        lineMin.Y = MathF.Round(lineMin.Y);
        ImGui.GetWindowDrawList().AddRectFilled(lineMin, lineMin + new Vector2(width, thickness), ImGui.GetColorU32(Cfg.AccentColor with { W = 0.45f }));
        ImGui.Dummy(new Vector2(width, thickness));
    }

    // ------------------------------------------------------------ Barres

    private void DrawRows(EncounterView view, float width)
    {
        var limit = Math.Max(1, Cfg.MaxRows);
        var top = (float)Math.Max(1, view.TopDamage);

        for (var i = 0; i < view.Combatants.Count && i < limit; i++)
            DrawRow(view.Combatants[i], i, top, width);

        // Toi au-delà de la limite de lignes : tu restes visible, à ton vrai rang.
        var localIndex = view.Combatants.ToList().FindIndex(c => c.IsLocal);
        if (localIndex >= limit)
            DrawRow(view.Combatants[localIndex], localIndex, top, width);
    }

    private void DrawRow(CombatantView row, int rank, float topDamage, float width)
    {
        var scale = ImGuiHelpers.GlobalScale * Math.Clamp(Cfg.HudScale, 0.6f, 2f);
        var lineH = ImGui.GetTextLineHeight();
        var height = MathF.Round(lineH + 4f * scale);

        ImGui.Dummy(new Vector2(width, height));
        var min = ImGui.GetItemRectMin();
        var max = min + new Vector2(width, height);
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        var rounding = 3f * scale;

        // Fond sombre pour garder la barre lisible même sans fond de HUD.
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.04f, 0.07f, 0.11f, hovered ? 0.70f : 0.45f)), rounding);

        // Remplissage, relatif au meilleur DPS du combat.
        var fraction = Math.Clamp(row.Damage / topDamage, 0f, 1f);
        var shown = AnimateFraction(row.Id, fraction);
        var color = BarColor(row);
        if (shown * width > 1f)
        {
            var fillMax = new Vector2(min.X + width * shown, max.Y);
            var deep = Vector4.Lerp(color, new Vector4(0f, 0f, 0f, color.W), 0.25f);
            drawList.AddRectFilledMultiColor(min, fillMax,
                ImGui.GetColorU32(deep), ImGui.GetColorU32(color), ImGui.GetColorU32(color), ImGui.GetColorU32(deep));
        }

        // Ta ligne est soulignée d'un contour.
        if (row.IsLocal)
            drawList.AddRect(min, max, ImGui.GetColorU32(Cfg.AccentColor with { W = 0.85f }), rounding, ImDrawFlags.None, Math.Max(1f, scale));

        var textY = min.Y + (height - lineH) / 2f;
        var pad = 4f * scale;
        var nameX = min.X + pad;

        if (Cfg.ShowJobIcons && DrawJobIcon(row.JobId, new Vector2(nameX, min.Y + (height - lineH) / 2f), lineH))
            nameX += lineH + pad * 0.75f;

        // Valeurs à droite, nom à gauche (coupé s'il déborde).
        var right = RightText(row);
        var rightW = ImGui.CalcTextSize(right).X;
        DrawText(new Vector2(max.X - pad - rightW, textY), new Vector4(1f, 1f, 1f, 1f), right);

        var name = plugin.DisplayName(row);
        drawList.PushClipRect(new Vector2(nameX, min.Y), new Vector2(max.X - pad * 2f - rightW, max.Y), true);
        DrawText(new Vector2(nameX, textY), new Vector4(1f, 1f, 1f, 1f), $"{rank + 1}. {name}");
        drawList.PopClipRect();

        if (hovered)
            ImGui.SetTooltip(Formatting.Tooltip(row, name, plugin.JobAbbreviation(row.JobId)));
    }

    private string RightText(CombatantView row)
    {
        var text = Formatting.Compact(row.Dps);
        if (Cfg.ShowTotalDamage)
            text = $"{Formatting.Compact(row.Damage)}  {text}";
        if (Cfg.ShowPercent)
            text = $"{text}  {Formatting.Percent(row.Share)}";
        return text;
    }

    private Vector4 BarColor(CombatantView row)
    {
        var alpha = Math.Clamp(Cfg.BarOpacity, 0.05f, 1f);
        if (row.Id == JobColors.LimitBreakId)
            return new Vector4(1f, 0.80f, 0.35f, alpha);

        var color = Cfg.UseJobColors ? JobColors.Of(row.JobId) ?? Cfg.AccentColor : Cfg.AccentColor;
        return color with { W = alpha };
    }

    private float AnimateFraction(uint id, float target)
    {
        var dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);
        if (!shownFractions.TryGetValue(id, out var value))
            value = target;

        value += (target - value) * (1f - MathF.Exp(-dt * 8f));
        if (MathF.Abs(target - value) < 0.001f)
            value = target;

        shownFractions[id] = value;
        return value;
    }

    private static bool DrawJobIcon(uint jobId, Vector2 pos, float size)
    {
        if (jobId == 0)
            return false;

        var texture = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(JobIconBase + jobId));
        if (!texture.TryGetWrap(out var wrap, out _) || wrap == null)
            return false;

        ImGui.GetWindowDrawList().AddImage(wrap.Handle, pos, pos + new Vector2(size, size));
        return true;
    }

    // ------------------------------------------------------------ Texte lisible

    /// <summary>Texte normal, avec un contour sombre si l'option est activée.</summary>
    private void Text(string text)
    {
        ImGui.TextUnformatted(text);
        var pos = ImGui.GetItemRectMin();
        DrawOutline(pos, text);
        if (Cfg.TextOutline && Cfg.OutlineStrength > 0f)
            ImGui.GetWindowDrawList().AddText(pos, ImGui.GetColorU32(ImGuiCol.Text), text);
    }

    /// <summary>Texte secondaire (durée, compteurs) : plus clair que le gris d'ImGui.</summary>
    private void Secondary(string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, Cfg.SecondaryColor))
            Text(text);
    }

    private void DrawText(Vector2 pos, Vector4 color, string text)
    {
        DrawOutline(pos, text);
        ImGui.GetWindowDrawList().AddText(pos, ImGui.GetColorU32(color), text);
    }

    /// <summary>Contour : 8 copies sombres décalées, sous le texte.</summary>
    private void DrawOutline(Vector2 pos, string text)
    {
        if (!Cfg.TextOutline || Cfg.OutlineStrength <= 0f)
            return;

        var drawList = ImGui.GetWindowDrawList();
        var shadow = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, Math.Clamp(Cfg.OutlineStrength, 0f, 1f)));
        var t = Math.Max(1f, ImGuiHelpers.GlobalScale * Cfg.HudScale);

        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (dx != 0 || dy != 0)
                    drawList.AddText(pos + new Vector2(dx * t, dy * t), shadow, text);
            }
        }
    }

    // ------------------------------------------------------------ Menu rapide

    private void DrawContextMenu()
    {
        const string menuId = "##DPSmeterHudMenu";
        if (ImGui.IsWindowHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
            ImGui.OpenPopup(menuId);

        if (!ImGui.BeginPopup(menuId))
            return;

        if (ImGui.MenuItem("Réglages"))
            plugin.ToggleConfigUi();

        ImGui.Separator();

        if (plugin.Tracker.IsActive && ImGui.MenuItem("Terminer le combat"))
            plugin.Tracker.EndCurrent(DateTime.UtcNow);

        if (ImGui.MenuItem("Remettre à zéro"))
            plugin.ClearAll();

        ImGui.Separator();

        if (ImGui.MenuItem(Cfg.HudLocked ? "Déverrouiller la position" : "Verrouiller la position"))
            Toggle(c => c.HudLocked = !c.HudLocked);

        if (ImGui.MenuItem(Cfg.HudCollapsed ? "Déplier le HUD" : "Replier le HUD"))
            Toggle(c => c.HudCollapsed = !c.HudCollapsed);

        ImGui.Separator();

        if (ImGui.MenuItem("Masquer le HUD (/dps pour le réafficher)"))
            plugin.ToggleHud();

        ImGui.EndPopup();
    }

    private void Toggle(Action<Configuration> change)
    {
        change(Cfg);
        Cfg.Save();
    }
}
