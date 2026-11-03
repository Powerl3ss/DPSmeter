using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
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

    // Hauteur de l'icône de job et de la tête de mort dans une ligne : la même pour les deux.
    private const float IconHeightInRow = 0.70f;

    private readonly Plugin plugin;
    private Configuration Cfg => plugin.Configuration;

    // Largeur affichée de chaque barre : elle glisse vers sa nouvelle valeur.
    private readonly Dictionary<uint, float> shownFractions = new();

    // Lignes de la mesure affichée, recalculées quand le combat ou la mesure change.
    private EncounterView? rowsView;
    private MeterMetric rowsMetric;
    private List<CombatantView> rows = new();

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
        if (HiddenByGame)
            return false;

        if (plugin.PreviewMode)
            return true;

        if (Plugin.CurrentCharacterId == 0)
            return false;

        if (Cfg.OnlyInDuty && !Plugin.Condition[ConditionFlag.BoundByDuty])
            return false;

        return !Cfg.HideOutOfCombat || plugin.RecentlyInCombat;
    }

    /// <summary>Cinématique ou écran de chargement : le HUD n'a rien à faire à l'écran.</summary>
    private static bool HiddenByGame =>
        Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
        Plugin.Condition[ConditionFlag.WatchingCutscene] ||
        Plugin.Condition[ConditionFlag.WatchingCutscene78] ||
        Plugin.Condition[ConditionFlag.BetweenAreas] ||
        Plugin.Condition[ConditionFlag.BetweenAreas51];

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
            Tip.Show((Cfg.HudCollapsed ? "Clic pour déplier le HUD" : "Clic pour replier le HUD") + "\nClic droit : réglages et options");

        // Durée et DPS toujours visibles : à zéro tant qu'aucun combat n'a eu lieu.
        var summary = view != null
            ? $"{Formatting.Duration(view.DurationSeconds)}  |  {MetricPrefix()}{Formatting.Compact(view.TotalOf(Cfg.HudMetric) / view.DurationSeconds)}"
            : $"{Formatting.Duration(0)}  |  {MetricPrefix()}{Formatting.Compact(0)}";
        var summaryWidth = ImGui.CalcTextSize(summary).X;
        var y = start.Y + (titleHeight - ImGui.GetTextLineHeight()) / 2f;
        ImGui.SetCursorPos(new Vector2(start.X + width - summaryWidth, y));
        Secondary(summary);
        if (ImGui.IsItemHovered())
        {
            if (view != null)
            {
                var outcome = Formatting.OutcomeText(view.Outcome, view.RemainingHp);
                var state = view.IsActive ? "En cours" : outcome.Length > 0 ? outcome : "Terminé";
                var healing = view.TotalHealing > 0 ? $"\n{Formatting.Full(view.TotalHealing)} soins" : string.Empty;
                var taken = view.TotalDamageTaken > 0 ? $"\n{Formatting.Full(view.TotalDamageTaken)} dégâts subis" : string.Empty;
                Tip.Show($"{view.Title}\n{state}  |  {Formatting.Duration(view.DurationSeconds)}\n{Formatting.Full(view.TotalDps)} DPS du groupe\n{Formatting.Full(view.TotalDamage)} dégâts{healing}{taken}");
            }
            else
            {
                Tip.Show("En attente d'un combat");
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

    private string MetricPrefix() => Cfg.HudMetric switch
    {
        MeterMetric.Healing => "Soins ",
        MeterMetric.DamageTaken => "Subis ",
        _ => string.Empty,
    };

    private void DrawRows(EncounterView view, float width)
    {
        var metric = Cfg.HudMetric;

        // Un autre classement seulement quand la photo du combat ou la mesure change, pas à chaque image.
        if (!ReferenceEquals(rowsView, view) || rowsMetric != metric)
        {
            rowsView = view;
            rowsMetric = metric;
            rows = view.RowsFor(metric);
        }

        if (rows.Count == 0)
        {
            Secondary($"{Formatting.MetricLabel(metric)} : rien pour ce combat");
            return;
        }

        var limit = Math.Max(1, Cfg.MaxRows);
        var top = (float)Math.Max(1, rows[0].ValueOf(metric));

        for (var i = 0; i < rows.Count && i < limit; i++)
            DrawRow(view, rows[i], i, top, width);

        // Toi au-delà de la limite de lignes : tu restes visible, à ton vrai rang.
        var localIndex = rows.FindIndex(c => c.IsLocal);
        if (localIndex >= limit)
            DrawRow(view, rows[localIndex], localIndex, top, width);
    }

    private void DrawRow(EncounterView view, CombatantView row, int rank, float topValue, float width)
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

        // Remplissage, relatif au meilleur de la mesure affichée.
        var fraction = Math.Clamp(row.ValueOf(Cfg.HudMetric) / topValue, 0f, 1f);
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

        var iconSize = MathF.Round(height * IconHeightInRow);
        if (Cfg.ShowJobIcons && DrawJobIcon(row.JobId, new Vector2(nameX, MathF.Round(min.Y + (height - iconSize) / 2f)), iconSize))
            nameX += iconSize + pad * 0.75f;

        // Valeurs à droite, nom à gauche (coupé s'il déborde).
        var white = new Vector4(1f, 1f, 1f, 1f);
        var right = RightText(view, row);
        var rightW = ImGui.CalcTextSize(right).X;
        DrawText(new Vector2(max.X - pad - rightW, textY), white, right);

        var nameEnd = max.X - pad * 2f - rightW;
        if (Cfg.ShowDeaths && plugin.IsDeadNow(row))
            nameEnd = DrawDeathIcon(nameEnd, min.Y, height, iconSize, pad);

        var name = plugin.DisplayName(row);
        drawList.PushClipRect(new Vector2(nameX, min.Y), new Vector2(nameEnd, max.Y), true);
        DrawText(new Vector2(nameX, textY), white, $"{rank + 1}. {name}");
        drawList.PopClipRect();

        if (hovered)
            Tip.Show(Formatting.Tooltip(row, name, plugin.JobAbbreviation(row.JobId), view.DurationSeconds, Cfg.RecentWindowSeconds));
    }

    /// <summary>
    /// Tête de mort à gauche des valeurs, tant que le joueur est mort. Sa taille est calculée d'après le
    /// dessin réel de l'icône : elle reste centrée dans la ligne sans en dépasser. Renvoie la nouvelle limite du nom.
    /// </summary>
    private unsafe float DrawDeathIcon(float right, float rowTop, float rowHeight, float iconSize, float pad)
    {
        const char Skull = (char)FontAwesomeIcon.Skull;

        var textScale = Math.Clamp(Cfg.HudScale, 0.6f, 2f);
        var icon = Skull.ToString();
        var x = right;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var font = ImGui.GetFont();
            var glyph = font.FindGlyph(Skull);
            if (glyph != null && glyph->Y1 > glyph->Y0 && font.FontSize > 0f)
            {
                // Taille pour que l'icône occupe la hauteur voulue, puis position de son haut réel.
                var drawnScale = iconSize / (glyph->Y1 - glyph->Y0);
                ImGui.SetWindowFontScale(textScale * drawnScale / (ImGui.GetFontSize() / font.FontSize));
                var scale = ImGui.GetFontSize() / font.FontSize;
                var top = rowTop + (rowHeight - (glyph->Y1 - glyph->Y0) * scale) / 2f - glyph->Y0 * scale;

                x = right - ImGui.CalcTextSize(icon).X;
                DrawText(new Vector2(x, top), new Vector4(1f, 0.47f, 0.47f, 1f), icon);
                ImGui.SetWindowFontScale(textScale);
            }
        }

        return x - pad;
    }

    private string RightText(EncounterView view, CombatantView row)
    {
        var metric = Cfg.HudMetric;
        var value = row.ValueOf(metric);
        var parts = new List<string>(4);

        if (Cfg.ShowTotalDamage)
            parts.Add(Formatting.Compact(value));

        parts.Add(Formatting.Compact(value / view.DurationSeconds));

        // DPS récent : seulement sur les dégâts et pendant le combat, ensuite il ne veut plus rien dire.
        if (metric == MeterMetric.Damage && Cfg.ShowRecentDps && view.IsActive)
            parts.Add(Formatting.Compact(row.RecentDps));

        if (Cfg.ShowPercent)
            parts.Add(Formatting.Percent(value / (float)Math.Max(1, view.TotalOf(metric))));

        return string.Join(" | ", parts);
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

        // Ce qu'on change le plus souvent d'abord : la mesure, puis le combat, puis le HUD lui-même.
        ImGui.TextDisabled("Afficher");
        foreach (var metric in Enum.GetValues<MeterMetric>())
        {
            if (ImGui.MenuItem(Formatting.MetricLabel(metric), Cfg.HudMetric == metric))
                Toggle(c => c.HudMetric = metric);
        }

        ImGui.Separator();

        ImGui.TextDisabled("Combat");
        if (plugin.Tracker.IsActive && ImGui.MenuItem("Terminer le combat"))
            plugin.Tracker.EndCurrent(DateTime.UtcNow);

        if (ImGui.MenuItem("Remettre à zéro"))
            plugin.ClearAll();

        ImGui.Separator();

        ImGui.TextDisabled("HUD");
        if (ImGui.MenuItem("Position verrouillée", Cfg.HudLocked))
            Toggle(c => c.HudLocked = !c.HudLocked);

        if (ImGui.MenuItem("Masquer le HUD", "/dps"))
            plugin.ToggleHud();

        if (ImGui.MenuItem("Réglages", "/dps config"))
            plugin.ToggleConfigUi();

        ImGui.EndPopup();
    }

    private void Toggle(Action<Configuration> change)
    {
        change(Cfg);
        Cfg.Save();
    }
}
