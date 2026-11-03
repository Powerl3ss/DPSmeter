using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace DPSmeter.Windows;

/// <summary>
/// Infobulles au style commun à DailyToDo, DPSmeter et BetterChat : fond sombre arrondi,
/// fin contour de la couleur d'accent, titre en couleur d'accent, lignes avec icône,
/// aide des clics plus discrète en bas.
/// </summary>
public static class Tip
{
    /// <summary>Couleur d'accent du plugin (réglée au démarrage du plugin).</summary>
    public static Func<Vector4> AccentProvider { get; set; } = () => new Vector4(0.30f, 0.65f, 1.00f, 1.00f);

    public static Vector4 Accent => AccentProvider() with { W = 1f };

    public static readonly Vector4 Text = new(0.92f, 0.94f, 0.97f, 1f);
    public static readonly Vector4 Dim = new(0.60f, 0.65f, 0.73f, 1f);
    public static readonly Vector4 Good = new(0.45f, 0.86f, 0.56f, 1f);
    public static readonly Vector4 Warn = new(1.00f, 0.76f, 0.36f, 1f);
    public static readonly Vector4 Bad = new(1.00f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 Background = new(0.07f, 0.09f, 0.13f, 0.97f);

    /// <summary>Marge intérieure de l'infobulle (à l'échelle de l'interface).</summary>
    public static Vector2 Padding => new Vector2(12f, 10f) * ImGuiHelpers.GlobalScale;

    /// <summary>Espacement entre les lignes de l'infobulle (à l'échelle de l'interface).</summary>
    public static Vector2 Spacing => new Vector2(6f, 5f) * ImGuiHelpers.GlobalScale;

    private static float S => ImGuiHelpers.GlobalScale;

    // ------------------------------------------------------------------ Ouverture

    /// <summary>Ouvre une infobulle stylée ; tout ce qui est dessiné jusqu'au Dispose s'y affiche.</summary>
    public static IDisposable Begin()
    {
        var style = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Padding)
                          .Push(ImGuiStyleVar.WindowRounding, 8f * S)
                          .Push(ImGuiStyleVar.WindowBorderSize, 1f)
                          .Push(ImGuiStyleVar.ItemSpacing, Spacing)
                          .Push(ImGuiStyleVar.Alpha, 1f);
        var colors = ImRaii.PushColor(ImGuiCol.PopupBg, Background)
                           .Push(ImGuiCol.Border, Accent with { W = 0.45f })
                           .Push(ImGuiCol.Separator, new Vector4(1f, 1f, 1f, 0.10f))
                           .Push(ImGuiCol.Text, Text);
        ImGui.BeginTooltip();
        ImGui.SetWindowFontScale(1f);
        return new Scope(style, colors);
    }

    /// <summary>
    /// Même apparence pour une fenêtre surgissante (menu, liste de choix) : à pousser avant
    /// ImGui.BeginPopup et à retirer après ImGui.EndPopup.
    /// </summary>
    public static IDisposable PushPopupStyle()
    {
        var style = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(6f, 6f) * S)
                          .Push(ImGuiStyleVar.PopupRounding, 8f * S)
                          .Push(ImGuiStyleVar.PopupBorderSize, 1f)
                          .Push(ImGuiStyleVar.ItemSpacing, new Vector2(6f, 2f) * S);
        var colors = ImRaii.PushColor(ImGuiCol.PopupBg, Background)
                           .Push(ImGuiCol.Border, Accent with { W = 0.45f })
                           .Push(ImGuiCol.Separator, new Vector4(1f, 1f, 1f, 0.10f))
                           .Push(ImGuiCol.Text, Text);
        return new PopupScope(style, colors);
    }

    private sealed class PopupScope(IDisposable style, IDisposable colors) : IDisposable
    {
        public void Dispose()
        {
            colors.Dispose();
            style.Dispose();
        }
    }

    private sealed class Scope(IDisposable style, IDisposable colors) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            ImGui.EndTooltip();
            colors.Dispose();
            style.Dispose();
        }
    }

    /// <summary>
    /// Remplace ImGui.SetTooltip : même texte, avec le style commun. S'il y a plusieurs lignes,
    /// la première sert de titre ; les lignes d'aide (« Clic : ... », « Ctrl + clic : ... »)
    /// passent en bas, plus discrètes, avec une icône de souris.
    /// </summary>
    public static void Show(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var content = new List<string>();
        var hints = new List<string>();
        foreach (var raw in text.Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (IsHint(line))
                hints.Add(line.Trim());
            else
                content.Add(line);
        }

        // Pas de lignes vides en début ou en fin.
        while (content.Count > 0 && content[0].Length == 0)
            content.RemoveAt(0);
        while (content.Count > 0 && content[^1].Length == 0)
            content.RemoveAt(content.Count - 1);

        using (Begin())
        {
            for (var i = 0; i < content.Count; i++)
            {
                var line = content[i];
                if (line.Length == 0)
                    ImGui.Dummy(new Vector2(0, 2f * S));
                else if (i == 0 && content.Count > 1)
                    ImGui.TextColored(Accent, line);
                else
                    ImGui.TextUnformatted(line);
            }

            if (hints.Count > 0)
            {
                if (content.Count > 0)
                    ImGui.Dummy(new Vector2(0, 1f * S));
                foreach (var hint in hints)
                    Hint(hint);
            }
        }
    }

    private static bool IsHint(string line)
    {
        var l = line.TrimStart();
        return l.StartsWith("Clic", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("Ctrl", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("Double-clic", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("Maj +", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("Tab ", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("Tab :", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ Éléments

    /// <summary>Titre de l'infobulle, dans la couleur d'accent.</summary>
    public static void Title(string text) => ImGui.TextColored(Accent, text);

    /// <summary>Texte secondaire, plus discret.</summary>
    public static void Note(string text) => ImGui.TextColored(Dim, text);

    /// <summary>Aide d'un clic ou d'un raccourci : discrète, avec une icône de souris ou de clavier.</summary>
    public static void Hint(string text)
    {
        var keyboard = text.StartsWith("Tab", StringComparison.OrdinalIgnoreCase) ||
                       text.StartsWith("Maj", StringComparison.OrdinalIgnoreCase);
        Line(keyboard ? FontAwesomeIcon.Keyboard : FontAwesomeIcon.MousePointer, Dim, text, color: Dim);
    }

    /// <summary>Ligne avec une icône colorée, un texte, puis un détail plus discret.</summary>
    public static void Line(FontAwesomeIcon icon, Vector4 iconColor, string text, string? detail = null, Vector4? color = null)
    {
        var lineH = ImGui.GetTextLineHeight();
        var iconBox = lineH * 1.15f;
        var start = ImGui.GetCursorScreenPos();
        var glyph = icon.ToIconString();

        // Icône centrée dans une colonne fixe, pour que tous les textes soient alignés.
        ImGui.Dummy(new Vector2(iconBox, lineH));
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var size = ImGui.CalcTextSize(glyph);
            var scale = Math.Min(1f, lineH * 0.9f / Math.Max(size.Y, 1f));
            var drawSize = size * scale;
            var pos = start + new Vector2((iconBox - drawSize.X) / 2f, (lineH - drawSize.Y) / 2f);
            ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, pos, ImGui.GetColorU32(iconColor), glyph);
        }

        ImGui.SameLine(0, 6f * S);
        if (color is { } c)
            ImGui.TextColored(c, text);
        else
            ImGui.TextUnformatted(text);

        if (!string.IsNullOrEmpty(detail))
        {
            ImGui.SameLine(0, 6f * S);
            ImGui.TextColored(Dim, detail);
        }
    }

    /// <summary>Petite étiquette arrondie (« Fait », « 2/3 », « En cours »...).</summary>
    public static void Pill(string text, Vector4 color)
    {
        var pad = new Vector2(7f, 1f) * S;
        var size = ImGui.CalcTextSize(text) + pad * 2f;
        var pos = ImGui.GetCursorScreenPos();
        var lineH = ImGui.GetTextLineHeight();
        var dl = ImGui.GetWindowDrawList();
        ImGui.Dummy(new Vector2(size.X, lineH));
        var min = new Vector2(pos.X, pos.Y + (lineH - size.Y) / 2f);
        dl.AddRectFilled(min, min + size, ImGui.GetColorU32(color with { W = 0.18f }), size.Y / 2f);
        dl.AddRect(min, min + size, ImGui.GetColorU32(color with { W = 0.55f }), size.Y / 2f, ImDrawFlags.None, 1f);
        dl.AddText(min + pad, ImGui.GetColorU32(color), text);
    }

    /// <summary>Titre suivi d'une étiquette d'état sur la même ligne.</summary>
    public static void Title(string text, string pill, Vector4 pillColor)
    {
        Title(text);
        ImGui.SameLine(0, 12f * S);
        Pill(pill, pillColor);
    }

    /// <summary>Une pastille par étape (3 pour le Mini Cactpot), ou une barre au-delà de 12.</summary>
    public static void Pips(int count, int target)
    {
        var accent = Accent;
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var h = MathF.Round(7f * S);
        var empty = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.10f));

        if (target <= 12)
        {
            var w = MathF.Round((target <= 4 ? 26f : 14f) * S);
            var gap = MathF.Round(4f * S);
            for (var i = 0; i < target; i++)
            {
                var min = pos + new Vector2(i * (w + gap), 0);
                dl.AddRectFilled(min, min + new Vector2(w, h), i < count ? ImGui.GetColorU32(accent) : empty, h / 2f);
            }
            ImGui.Dummy(new Vector2(target * (w + gap) - gap, h));
        }
        else
        {
            Bar((float)count / Math.Max(target, 1), 180f * S);
        }
    }

    /// <summary>Petite barre arrondie remplie de la couleur d'accent.</summary>
    public static void Bar(float fraction, float width, Vector4? color = null)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var h = MathF.Round(7f * S);
        dl.AddRectFilled(pos, pos + new Vector2(width, h), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.10f)), h / 2f);
        var fill = width * Math.Clamp(fraction, 0f, 1f);
        if (fill > 0)
            dl.AddRectFilled(pos, pos + new Vector2(MathF.Max(fill, h), h), ImGui.GetColorU32(color ?? Accent), h / 2f);
        ImGui.Dummy(new Vector2(width, h));
    }

    // ------------------------------------------------------------------ Menus (clic droit)

    // Largeur des lignes du menu ouvert : la plus grande mesurée à l'image précédente,
    // pour que toutes les lignes (et leurs raccourcis à droite) soient alignées.
    private static readonly Dictionary<string, float> MenuWidths = new();
    private static string? menuId;
    private static float menuNeeded;
    private static IDisposable? menuStyle;

    /// <summary>Ouvre un menu (clic droit) au style commun. À fermer avec EndMenu seulement s'il renvoie vrai.</summary>
    public static bool BeginMenu(string id)
    {
        var style = PushPopupStyle();
        if (!ImGui.BeginPopup(id))
        {
            style.Dispose();
            return false;
        }

        menuStyle = style;
        menuId = id;
        menuNeeded = 0f;
        ImGui.Dummy(new Vector2(0, 1f * S));
        return true;
    }

    public static void EndMenu()
    {
        ImGui.Dummy(new Vector2(0, 1f * S));
        if (menuId != null)
            MenuWidths[menuId] = menuNeeded;
        menuId = null;
        ImGui.EndPopup();
        menuStyle?.Dispose();
        menuStyle = null;
    }

    private static float MenuWidth => MathF.Max(MenuWidths.TryGetValue(menuId ?? string.Empty, out var w) ? w : 0f, 190f * S);
    private static float MenuRowHeight => MathF.Round(ImGui.GetTextLineHeight() + 9f * S);

    /// <summary>En-tête du menu (nom de l'onglet, du joueur, de l'objet...) en couleur d'accent.</summary>
    public static void MenuTitle(string text)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 10f * S);
        ImGui.TextColored(Accent, text);
        menuNeeded = MathF.Max(menuNeeded, ImGui.CalcTextSize(text).X + 20f * S);
        ImGui.Dummy(new Vector2(0, 1f * S));
    }

    /// <summary>Titre discret d'un groupe de lignes (« Tâches », « HUD »...).</summary>
    public static void MenuSection(string text)
    {
        ImGui.Dummy(new Vector2(0, 1f * S));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 10f * S);
        ImGui.TextColored(Dim, text.ToUpperInvariant());
        menuNeeded = MathF.Max(menuNeeded, ImGui.CalcTextSize(text.ToUpperInvariant()).X + 20f * S);
    }

    /// <summary>Séparation entre deux groupes du menu.</summary>
    public static void MenuSeparator()
    {
        ImGui.Dummy(new Vector2(0, 1f * S));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0, 1f * S));
    }

    /// <summary>
    /// Ligne de menu : icône, texte, puis à droite un raccourci, un interrupteur (option on/off)
    /// ou une coche (choix parmi plusieurs). Le menu se ferme au clic ; renvoie vrai si on a cliqué.
    /// </summary>
    public static bool MenuItem(string label, FontAwesomeIcon icon, string? shortcut = null,
                                bool? toggled = null, bool selected = false, bool danger = false)
    {
        var rowH = MenuRowHeight;
        var labelW = ImGui.CalcTextSize(label).X;
        var rightW = toggled.HasValue ? 26f * S : selected ? 14f * S : shortcut is { Length: > 0 } ? ImGui.CalcTextSize(shortcut).X : 0f;
        menuNeeded = MathF.Max(menuNeeded, 30f * S + labelW + (rightW > 0 ? 24f * S + rightW : 0f) + 12f * S);

        var width = MenuWidth;
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(width, rowH);
        var clicked = ImGui.InvisibleButton($"##menu{label}", new Vector2(width, rowH));
        var hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        var main = danger ? Bad : Text;

        // Fond au survol : teinté de la couleur d'accent (ou de rouge pour une action destructrice).
        if (hovered)
            dl.AddRectFilled(min, max, ImGui.GetColorU32((danger ? Bad : Accent) with { W = 0.16f }), 6f * S);

        var lineH = ImGui.GetTextLineHeight();
        var centerY = min.Y + rowH / 2f;

        // Icône dans une colonne fixe.
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = icon.ToIconString();
            var scale = 0.8f;
            var size = ImGui.CalcTextSize(glyph) * scale;
            var iconColor = danger ? Bad : hovered || selected || toggled == true ? Accent : Dim;
            dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale,
                       new Vector2(min.X + 14f * S - size.X / 2f, centerY - size.Y / 2f), ImGui.GetColorU32(iconColor), glyph);
        }

        dl.AddText(new Vector2(min.X + 30f * S, min.Y + (rowH - lineH) / 2f), ImGui.GetColorU32(main), label);

        var right = max.X - 10f * S;
        if (toggled is { } on)
        {
            // Petit interrupteur, comme dans les réglages.
            var trackW = 24f * S;
            var trackH = 13f * S;
            var tMin = new Vector2(right - trackW, centerY - trackH / 2f);
            var tMax = tMin + new Vector2(trackW, trackH);
            dl.AddRectFilled(tMin, tMax, ImGui.GetColorU32(on ? Accent : new Vector4(1f, 1f, 1f, 0.14f)), trackH / 2f);
            var knobR = trackH / 2f - 2f * S;
            var knobX = on ? tMax.X - trackH / 2f : tMin.X + trackH / 2f;
            dl.AddCircleFilled(new Vector2(knobX, centerY), knobR, ImGui.GetColorU32(on ? Background : Text with { W = 0.85f }), 16);
        }
        else if (selected)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var glyph = FontAwesomeIcon.Check.ToIconString();
                var scale = 0.8f;
                var size = ImGui.CalcTextSize(glyph) * scale;
                dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale,
                           new Vector2(right - size.X, centerY - size.Y / 2f), ImGui.GetColorU32(Accent), glyph);
            }
        }
        else if (shortcut is { Length: > 0 })
        {
            var size = ImGui.CalcTextSize(shortcut);
            dl.AddText(new Vector2(right - size.X, min.Y + (rowH - lineH) / 2f), ImGui.GetColorU32(Dim), shortcut);
        }

        if (clicked)
            ImGui.CloseCurrentPopup();
        return clicked;
    }

    /// <summary>Séparateur discret entre deux parties de l'infobulle.</summary>
    public static void Separator()
    {
        ImGui.Dummy(new Vector2(0, 1f * S));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0, 1f * S));
    }
}
