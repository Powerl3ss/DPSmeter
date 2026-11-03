using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using DPSmeter.Core;

namespace DPSmeter.Windows;

/// <summary>Fenêtre de réglages : en-tête, barre de navigation à gauche, pages, statut en bas.</summary>
public sealed partial class ConfigWindow : Window, IDisposable
{
    private enum Page { Home, History, Display, Measure, About }

    private static readonly (Page Page, FontAwesomeIcon Icon, string Label)[] Pages =
    {
        (Page.Home, FontAwesomeIcon.Home, "Accueil"),
        (Page.History, FontAwesomeIcon.History, "Historique"),
        (Page.Display, FontAwesomeIcon.Palette, "Affichage"),
        (Page.Measure, FontAwesomeIcon.Crosshairs, "Mesure"),
        (Page.About, FontAwesomeIcon.InfoCircle, "À propos"),
    };

    private const string RepoUrl = "https://github.com/Powerl3ss/DPSmeter";

    private readonly Plugin plugin;
    private Configuration Cfg => plugin.Configuration;

    private Page page = Page.Home;
    private int displaySection;

    // Styles de fenêtre poussés avant Begin (PreDraw) et retirés après End (PostDraw).
    private IDisposable? windowStyle;
    private IDisposable? windowColors;

    private Task<IDalamudTextureWrap?>? logoTask;

    public ConfigWindow(Plugin plugin)
        : base("DPSmeter | Réglages###DPSmeterSettings",
               ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        Size = new Vector2(880, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = ExpandedConstraints;

        // Vraies polices de grande taille (au lieu d'agrandir le texte, ce qui le rend flou).
        var atlas = Plugin.PluginInterface.UiBuilder.FontAtlas;
        // La police par défaut est dessinée à des tailles fixes : toute autre taille est étirée et devient floue.
        // 14 et 18 points (soit 18,67 et 24 pixels) sont des tailles exactes.
        titleFont = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(14f * 4f / 3f)));
        heroFont = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(24f)));
        numberFont = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(32f)));
    }

    private readonly IFontHandle titleFont;
    private readonly IFontHandle heroFont;
    private readonly IFontHandle numberFont;

    // Tailles en pixels "non mis à l'échelle" (Dalamud applique l'échelle de l'interface).
    private const float HeaderHeight = 44f;
    private static readonly WindowSizeConstraints ExpandedConstraints = new()
    {
        MinimumSize = new Vector2(780, 560),
        MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
    };
    private static readonly WindowSizeConstraints CollapsedConstraints = new()
    {
        MinimumSize = new Vector2(780, HeaderHeight),
        MaximumSize = new Vector2(float.MaxValue, HeaderHeight),
    };
    private static readonly WindowSizeConstraints FoldingConstraints = new()
    {
        MinimumSize = new Vector2(780, HeaderHeight),
        MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
    };

    // Fenêtre repliée : seul l'en-tête reste visible.
    private bool collapsed;
    private float expandedHeight;
    private Vector2 lastSize;

    private void ToggleWindowCollapsed()
    {
        if (folding)
            return;

        if (!collapsed)
        {
            expandedHeight = lastSize.Y;
            collapsed = true;
            StartFold(lastSize.Y, HeaderHeight * S);
        }
        else
        {
            collapsed = false;
            StartFold(lastSize.Y, Math.Max(expandedHeight, 560f * S));
        }
    }

    public override void OnClose()
    {
        // L'aperçu ne doit pas rester affiché une fois les réglages fermés.
        plugin.PreviewMode = false;
    }

    public void Dispose()
    {
        titleFont.Dispose();
        heroFont.Dispose();
        numberFont.Dispose();
        if (logoTask is { IsCompletedSuccessfully: true })
            logoTask.Result?.Dispose();
    }

    // =================================================================== Cadre

    public override void PreDraw()
    {
        windowStyle = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero)
                            .Push(ImGuiStyleVar.WindowRounding, 14f * S)
                            .Push(ImGuiStyleVar.WindowBorderSize, 1f);
        windowColors = ImRaii.PushColor(ImGuiCol.WindowBg, WindowBg)
                             .Push(ImGuiCol.Border, Line);

        // Repli / dépli animé de la fenêtre.
        ApplyFoldAnimation();
    }

    public override void PostDraw()
    {
        windowColors?.Dispose();
        windowColors = null;
        windowStyle?.Dispose();
        windowStyle = null;
    }

    public override void Draw()
    {
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 7f * S)
                                .Push(ImGuiStyleVar.GrabRounding, 7f * S)
                                .Push(ImGuiStyleVar.PopupRounding, 8f * S)
                                .Push(ImGuiStyleVar.ScrollbarRounding, 8f * S)
                                .Push(ImGuiStyleVar.ScrollbarSize, 10f * S)
                                .Push(ImGuiStyleVar.FramePadding, new Vector2(10f, 6f) * S)
                                .Push(ImGuiStyleVar.ItemSpacing, new Vector2(10f, 8f) * S)
                                .Push(ImGuiStyleVar.WindowPadding, new Vector2(10f, 10f) * S);
        using var colors = ImRaii.PushColor(ImGuiCol.Text, TextMain)
                                 .Push(ImGuiCol.TextDisabled, TextDim)
                                 .Push(ImGuiCol.ChildBg, Vector4.Zero)
                                 .Push(ImGuiCol.FrameBg, InputBg)
                                 .Push(ImGuiCol.FrameBgHovered, InputBgHover)
                                 .Push(ImGuiCol.FrameBgActive, InputBgHover)
                                 .Push(ImGuiCol.Button, InputBg)
                                 .Push(ImGuiCol.ButtonHovered, InputBgHover)
                                 .Push(ImGuiCol.ButtonActive, AccentSoft)
                                 .Push(ImGuiCol.Header, AccentSoft)
                                 .Push(ImGuiCol.HeaderHovered, AccentSoft)
                                 .Push(ImGuiCol.HeaderActive, AccentBorder)
                                 .Push(ImGuiCol.CheckMark, Accent)
                                 .Push(ImGuiCol.SliderGrab, Accent)
                                 .Push(ImGuiCol.SliderGrabActive, Vector4.Lerp(Accent, White, 0.2f))
                                 .Push(ImGuiCol.PopupBg, new Vector4(0.09f, 0.10f, 0.13f, 0.98f))
                                 .Push(ImGuiCol.ScrollbarBg, Vector4.Zero)
                                 .Push(ImGuiCol.ScrollbarGrab, new Vector4(1f, 1f, 1f, 0.12f))
                                 .Push(ImGuiCol.ScrollbarGrabHovered, new Vector4(1f, 1f, 1f, 0.20f))
                                 .Push(ImGuiCol.ScrollbarGrabActive, AccentBorder)
                                 .Push(ImGuiCol.Separator, Line);

        var size = ImGui.GetWindowSize();
        lastSize = size;
        var origin = ImGui.GetWindowPos();
        var headerH = HeaderHeight * S;
        var footerH = 44f * S;
        var sidebarW = 64f * S;
        var drawList = ImGui.GetWindowDrawList();

        DrawAnimatedBackground(origin, size);

        // Repliée, ou pas encore assez haute pendant l'animation : seulement l'en-tête.
        if (collapsed || size.Y < headerH + footerH + 80f * S)
        {
            DrawTitleBar(size, headerH);
            return;
        }

        drawList.AddRectFilled(origin + new Vector2(0, headerH), origin + new Vector2(sidebarW, size.Y - footerH), Col(SidebarBg));
        drawList.AddLine(origin + new Vector2(0, headerH), origin + new Vector2(size.X, headerH), Col(Line));
        drawList.AddLine(origin + new Vector2(sidebarW, headerH), origin + new Vector2(sidebarW, size.Y - footerH), Col(Line));
        drawList.AddLine(origin + new Vector2(0, size.Y - footerH), origin + new Vector2(size.X, size.Y - footerH), Col(Line));

        DrawTitleBar(size, headerH);
        DrawSidebar(headerH, sidebarW);

        ImGui.SetCursorPos(new Vector2(sidebarW + 1, headerH + 1));
        using (var child = ImRaii.Child("##page", new Vector2(size.X - sidebarW - 1, size.Y - headerH - footerH - 2), false))
        {
            if (child.Success)
                DrawPage();
        }

        DrawFooter(size, footerH);
    }

    private void DrawTitleBar(Vector2 size, float headerH)
    {
        var pad = 18f * S;
        var logoSize = 24f * S;

        ImGui.SetCursorPos(new Vector2(pad, (headerH - logoSize) / 2f));
        DrawLogo(logoSize);

        ImGui.SameLine(0, 12f * S);
        using (titleFont.Push())
        {
            ImGui.SetCursorPosY((headerH - ImGui.GetTextLineHeight()) / 2f);
            ImGui.TextUnformatted("DPSmeter");
        }
        var titleMin = ImGui.GetItemRectMin();
        var titleMax = ImGui.GetItemRectMax();

        var loggedIn = Plugin.CurrentCharacterId != 0;
        ImGui.SameLine(0, 12f * S);
        ImGui.SetCursorPosY((headerH - FrameH) / 2f);
        if (loggedIn)
            Pill(plugin.LocalName, Success, dot: true, centerInFrame: true);
        else
            Pill("Hors ligne", TextDim, dot: true, centerInFrame: true);

        ImGui.SetWindowFontScale(0.95f); // soleil / lune un peu plus petits que le titre

        // Soleil la journée, lune le soir (heure réelle du PC), à la taille du titre
        // et centré verticalement sur lui.
        ImGui.SameLine(0, 10f * S);
        var dayIcon = IsDaytime ? FontAwesomeIcon.Sun : FontAwesomeIcon.Moon;
        var box = titleMax.Y - titleMin.Y;
        var boxMin = new Vector2(ImGui.GetCursorScreenPos().X, (titleMin.Y + titleMax.Y) / 2f - box / 2f);
        ImGui.SetCursorScreenPos(boxMin);
        ImGui.Dummy(new Vector2(box, box));
        if (ImGui.IsItemHovered())
            Tip.Show(IsDaytime ? "Journée (8h00 - 20h00)" : "Soirée (20h00 - 8h00)");
        DrawIconCentered(dayIcon, boxMin, boxMin + new Vector2(box, box), IsDaytime ? SunColor : MoonColor);
        ImGui.SetWindowFontScale(1f);

        var buttonY = (headerH - FrameH) / 2f;
        ImGui.SetCursorPos(new Vector2(size.X - pad - FrameH, buttonY));
        if (IconButton("##close", FontAwesomeIcon.Times, "Fermer"))
            IsOpen = false;

        ImGui.SetCursorPos(new Vector2(size.X - pad - FrameH * 2 - 6f * S, buttonY));
        if (IconButton("##fold", collapsed ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronUp,
                       collapsed ? "Déplier les réglages (ou double-clic sur l'en-tête)" : "Replier les réglages (ou double-clic sur l'en-tête)"))
            ToggleWindowCollapsed();

        ImGui.SetCursorPos(new Vector2(size.X - pad - FrameH * 3 - 12f * S, buttonY));
        if (IconButton("##hud", Cfg.HudVisible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash,
                       Cfg.HudVisible ? "Masquer le HUD" : "Afficher le HUD"))
            plugin.ToggleHud();

        // Double-clic sur l'en-tête (hors boutons) : replie ou déplie la fenêtre.
        var origin = ImGui.GetWindowPos();
        var mouse = ImGui.GetMousePos();
        var inHeader = mouse.X >= origin.X && mouse.X < origin.X + size.X &&
                       mouse.Y >= origin.Y && mouse.Y < origin.Y + headerH;
        if (inHeader && ImGui.IsWindowHovered() && !ImGui.IsAnyItemHovered() &&
            ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            ToggleWindowCollapsed();
    }

    private void DrawSidebar(float headerH, float sidebarW)
    {
        var button = 40f * S;
        var gap = 8f * S;
        var x = (sidebarW - button) / 2f;
        var origin = ImGui.GetWindowPos();
        var drawList = ImGui.GetWindowDrawList();

        // Surlignage de la page active : il glisse jusqu'à la nouvelle page.
        var activeIndex = Array.FindIndex(Pages, p => p.Page == page);
        var slide = Animate(ImGui.GetID("##navHighlight"), activeIndex, 18f);
        var highlightMin = origin + new Vector2(x, headerH + 14f * S + slide * (button + gap));
        var highlightMax = highlightMin + new Vector2(button, button);
        drawList.AddRectFilled(highlightMin, highlightMax, Col(AccentSoft), 10f * S);
        drawList.AddRect(highlightMin, highlightMax, Col(AccentBorder), 10f * S);
        drawList.AddRectFilled(new Vector2(origin.X, highlightMin.Y + 9f * S), new Vector2(origin.X + 3f * S, highlightMax.Y - 9f * S), Col(Accent), 2f * S);

        for (var i = 0; i < Pages.Length; i++)
        {
            var (target, icon, label) = Pages[i];
            ImGui.SetCursorPos(new Vector2(x, headerH + 14f * S + i * (button + gap)));
            var min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(button, button);
            if (ImGui.InvisibleButton($"##nav{i}", new Vector2(button, button)))
                page = target;

            var hovered = ImGui.IsItemHovered();
            var active = page == target;
            if (!active && hovered)
                drawList.AddRectFilled(min, max, Col(HoverOverlay), 10f * S);

            DrawIconCentered(icon, min, max, active ? Accent : hovered ? TextMain : TextDim);
            if (hovered)
                Tip.Show(label);
        }
    }

    private void DrawFooter(Vector2 size, float footerH)
    {
        var pad = 18f * S;
        var status = FooterStatus();
        var statusSize = ImGui.CalcTextSize(status);
        ImGui.SetCursorPos(new Vector2(size.X - pad - statusSize.X, size.Y - footerH + (footerH - statusSize.Y) / 2f));
        ImGui.TextColored(TextDim, status);
    }

    private string FooterStatus()
    {
        if (plugin.Parser.HookError != null)
            return "Lecture des dégâts indisponible avec cette version du jeu";
        if (Plugin.CurrentCharacterId == 0)
            return "Connecte-toi en jeu pour mesurer tes dégâts";

        return plugin.Tracker.IsActive && plugin.View is { } view
            ? $"En combat  |  {Formatting.Duration(view.DurationSeconds)}  |  {Formatting.Compact(view.TotalDps)} DPS du groupe"
            : "En attente d'un combat";
    }

    private void DrawLogo(float size)
    {
        logoTask ??= LoadLogoAsync();
        if (logoTask is { IsCompletedSuccessfully: true, Result: { } logo })
        {
            ImGui.Image(logo.Handle, new Vector2(size, size));
            return;
        }

        // Le temps que le logo charge : pastille avec une icône de graphique.
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(size, size);
        ImGui.GetWindowDrawList().AddRectFilled(min, max, Col(Accent), 8f * S);
        DrawIconCentered(FontAwesomeIcon.ChartBar, min, max, OnAccent);
        ImGui.Dummy(new Vector2(size, size));
    }

    private static async Task<IDalamudTextureWrap?> LoadLogoAsync()
    {
        try
        {
            await using var stream = typeof(ConfigWindow).Assembly.GetManifestResourceStream("DPSmeter.icon.png");
            if (stream == null)
                return null;
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            return await Plugin.TextureProvider.CreateFromImageAsync(memory.ToArray());
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Logo introuvable");
            return null;
        }
    }

    private void DrawPage()
    {
        var pad = 24f * S;
        pageWidth = ImGui.GetContentRegionAvail().X - pad * 2;
        cardInner = pageWidth;

        // Transition : la nouvelle page apparaît en fondu en remontant légèrement.
        var pageBefore = lastPage;
        var appear = PageAppear();
        if (pageBefore != page)
            ImGui.SetScrollY(0);
        using var fade = ImRaii.PushStyle(ImGuiStyleVar.Alpha, Math.Max(0.02f, appear) * ImGui.GetStyle().Alpha);

        ImGui.SetCursorPos(new Vector2(pad, pad * 0.8f + (1f - appear) * 16f * S));
        ImGui.BeginGroup();
        switch (page)
        {
            case Page.Home: DrawHomePage(); break;
            case Page.History: DrawHistoryPage(); break;
            case Page.Display: DrawDisplayPage(); break;
            case Page.Measure: DrawMeasurePage(); break;
            case Page.About: DrawAboutPage(); break;
        }
        ImGui.EndGroup();
        ImGui.Dummy(new Vector2(0, pad));
    }

    // =================================================================== Accueil

    private void DrawHomePage()
    {
        var view = plugin.PreviewMode ? null : plugin.View;

        if (view == null || view.Combatants.Count == 0)
        {
            Hero("Prêt à mesurer.", "Le compteur démarre tout seul au premier coup porté.");
            Card(() =>
            {
                IconText(plugin.Parser.HookError == null ? FontAwesomeIcon.Crosshairs : FontAwesomeIcon.ExclamationTriangle,
                         plugin.Parser.HookError == null ? Accent : Danger);
                ImGui.SameLine();
                ImGui.TextUnformatted(plugin.Parser.HookError == null ? "En attente d'un combat" : "Lecture des dégâts indisponible");
                TextDimmed(plugin.Parser.HookError == null
                    ? "Lance-toi sur un ennemi ou un mannequin d'entraînement. Tes dégâts et ceux de ton groupe s'afficheront ici et dans le HUD."
                    : "Le jeu vient sans doute d'être mis à jour. Le plugin refonctionnera après la mise à jour de Dalamud.");
            });
            DrawHomeButtons(null);
            return;
        }

        var local = view.Local;
        var rank = local != null ? view.Combatants.ToList().FindIndex(c => c.IsLocal) + 1 : 0;
        Hero(view.Title,
             view.IsActive ? "Combat en cours" : "Dernier combat",
             Formatting.Duration(view.DurationSeconds),
             view.IsActive || view.Outcome == Outcome.Unknown ? (string.IsNullOrEmpty(view.ZoneName) ? null : view.ZoneName) : Formatting.OutcomeText(view.Outcome, view.RemainingHp));

        CardsRow(
            () => StatCard(FontAwesomeIcon.User, "Ton DPS", local != null ? Formatting.Compact(local.Dps) : "-",
                           local != null ? LocalCaption(view, local, rank) : "Aucun dégât de ta part"),
            () => StatCard(FontAwesomeIcon.Users, "DPS du groupe", Formatting.Compact(view.TotalDps),
                           $"{Formatting.Compact(view.TotalDamage)} dégâts au total"));

        SectionTitle("Détail", $"{view.Combatants.Count} combattant(s)");
        DrawEncounterDetails(view);

        ImGui.Dummy(new Vector2(0, 4f * S));
        DrawHomeButtons(view);
    }

    private string LocalCaption(EncounterView view, CombatantView local, int rank)
    {
        var caption = $"{Formatting.Percent(local.Share)} des dégâts  |  rang {rank}/{view.Combatants.Count}";
        return BestFor(view) is { } best ? $"{caption}  |  record {Formatting.Compact(best.BestDps)}" : caption;
    }

    private void DrawHomeButtons(EncounterView? view)
    {
        if (view is { IsActive: true })
        {
            if (FancyButton("##homeEnd", "Terminer le combat", FontAwesomeIcon.Stop, ButtonSize("Terminer le combat", FontAwesomeIcon.Stop), primary: false))
                plugin.Tracker.EndCurrent(DateTime.UtcNow);
            ImGui.SameLine();
        }

        if (FancyButton("##homeHistory", "Historique", FontAwesomeIcon.History, ButtonSize("Historique", FontAwesomeIcon.History), primary: false))
            page = Page.History;

        if (view == null)
            return;

        ImGui.SameLine();
        if (FancyButton("##homeClear", "Remettre à zéro", FontAwesomeIcon.Undo, ButtonSize("Remettre à zéro", FontAwesomeIcon.Undo), primary: false, danger: true) &&
            ImGui.GetIO().KeyCtrl)
            plugin.ClearAll();
        if (ImGui.IsItemHovered())
            Tip.Show("Ctrl + clic : remet le HUD à zéro. Les combats enregistrés sont conservés.");
    }

    private void StatCard(FontAwesomeIcon icon, string title, string value, string caption)
    {
        IconText(icon, Accent);
        ImGui.SameLine();
        ImGui.TextColored(TextDim, title);

        using (numberFont.Push())
            ImGui.TextUnformatted(value);

        TextDimmed(caption);
    }

    // =================================================================== Affichage

    private void DrawDisplayPage()
    {
        Hero("Affichage", "Règle l'apparence et le comportement du HUD.",
             Cfg.HudVisible ? "HUD affiché" : "HUD masqué",
             Cfg.HudLocked ? "Position verrouillée" : "Position libre");

        displaySection = Segmented("##display", new[]
        {
            ("HUD", FontAwesomeIcon.WindowMaximize),
            ("Couleurs", FontAwesomeIcon.Palette),
        }, displaySection);

        var changed = false;
        switch (displaySection)
        {
            case 0:
                Card(() =>
                {
                    changed |= ToggleRow("Afficher le HUD", "Raccourci : /dps", Cfg.HudVisible, v =>
                    {
                        if (v != Cfg.HudVisible)
                            plugin.ToggleHud();
                    });
                    ToggleRow("Aperçu avec de faux chiffres", "Pour placer et régler le HUD sans te battre. S'arrête à la fermeture des réglages.", plugin.PreviewMode, v => plugin.PreviewMode = v);
                    changed |= ToggleRow("Verrouiller la position", "Empêche de déplacer le HUD par erreur.", Cfg.HudLocked, v => Cfg.HudLocked = v);
                    using (ImRaii.Disabled(!Cfg.HudLocked))
                        changed |= ToggleRow("Clics traversants", "Les clics passent à travers le HUD (demande une position verrouillée).", Cfg.ClickThrough, v => Cfg.ClickThrough = v);
                    changed |= ToggleRow("HUD replié", "Ne montre que la ligne de titre. Clic sur le titre du HUD pour le rouvrir.", Cfg.HudCollapsed, v => Cfg.HudCollapsed = v);
                });

                SectionTitle("Contenu des barres");
                Card(() =>
                {
                    changed |= ToggleRow("Icônes de jobs", null, Cfg.ShowJobIcons, v => Cfg.ShowJobIcons = v);
                    var metric = (int)Cfg.HudMetric;
                    SettingRow("Mesure affichée", "Aussi dans le menu du clic droit sur le HUD.", 320f * S, () =>
                    {
                        var chosen = Segmented("##hudMetric", new[]
                        {
                            ("Dégâts", FontAwesomeIcon.Fire),
                            ("Soins", FontAwesomeIcon.Heart),
                            ("Subis", FontAwesomeIcon.ShieldAlt),
                        }, metric, 320f * S);
                        if (chosen != metric)
                        {
                            Cfg.HudMetric = (MeterMetric)chosen;
                            changed = true;
                        }
                    }, FrameH + 20f * S);
                    changed |= ToggleRow("DPS récent", "En combat, le DPS des dernières secondes s'ajoute à la moyenne. La durée se règle dans l'onglet Mesure.", Cfg.ShowRecentDps, v => Cfg.ShowRecentDps = v);
                    changed |= ToggleRow("Morts", "Une tête de mort sur la barre d'un joueur, tant qu'il est mort.", Cfg.ShowDeaths, v => Cfg.ShowDeaths = v);
                    changed |= ToggleRow("Part des dégâts", "Le pourcentage à droite de chaque barre.", Cfg.ShowPercent, v => Cfg.ShowPercent = v);
                    changed |= ToggleRow("Dégâts totaux", "Le total, en plus du DPS.", Cfg.ShowTotalDamage, v => Cfg.ShowTotalDamage = v);
                    changed |= ToggleRow("Masquer le nom des autres", "Affiche leur job à la place, pratique pour une capture d'écran.", Cfg.HideOtherNames, v => Cfg.HideOtherNames = v);
                    changed |= SliderIntRow("Nombre de lignes", "Tu restes toujours visible, même au-delà de cette limite.", Cfg.MaxRows, 1, 24, v => Cfg.MaxRows = v);
                });

                SectionTitle("Masquer le HUD automatiquement");
                Card(() =>
                {
                    changed |= ToggleRow("Hors combat", "Le HUD apparaît en combat, puis disparaît après le délai ci-dessous.", Cfg.HideOutOfCombat, v => Cfg.HideOutOfCombat = v);
                    using (ImRaii.Disabled(!Cfg.HideOutOfCombat))
                        changed |= SliderIntRow("Délai après le combat", "En secondes, pour laisser le temps de lire le résultat.", Cfg.HideDelaySeconds, 0, 120, v => Cfg.HideDelaySeconds = v);
                    changed |= ToggleRow("Hors instance", "N'affiche le HUD que dans les donjons, raids, défis...", Cfg.OnlyInDuty, v => Cfg.OnlyInDuty = v);
                });

                SectionTitle("Taille et transparence");
                Card(() =>
                {
                    changed |= SliderRow("Largeur", null, Cfg.HudWidth, 160f, 700f, "%.0f", v => Cfg.HudWidth = v);
                    changed |= SliderRow("Taille du texte", null, Cfg.HudScale, 0.6f, 2f, "x%.2f", v => Cfg.HudScale = v);
                    changed |= SliderRow("Opacité du fond", "0 = fond complètement retiré.", Cfg.HudOpacity, 0f, 1f, "%.2f", v => Cfg.HudOpacity = v);
                    changed |= SliderRow("Opacité des barres", null, Cfg.BarOpacity, 0.05f, 1f, "%.2f", v => Cfg.BarOpacity = v);
                });
                break;

            default:
                Card(() =>
                {
                    CardLabel("Couleurs");
                    changed |= ToggleRow("Une couleur par job", "Désactivé : toutes les barres prennent la couleur d'accent.", Cfg.UseJobColors, v => Cfg.UseJobColors = v);
                    changed |= ColorRow("Couleur d'accent", "Titre, contour de ta barre, réglages.", Cfg.AccentColor, v => Cfg.AccentColor = v);
                    changed |= ColorRow("Texte secondaire", "Durée et DPS du groupe.", Cfg.SecondaryColor, v => Cfg.SecondaryColor = v);
                });
                Card(() =>
                {
                    CardLabel("Lisibilité");
                    changed |= ToggleRow("Contour sombre autour du texte", "Garde le texte lisible sur les barres et sans fond.", Cfg.TextOutline, v => Cfg.TextOutline = v);
                    using (ImRaii.Disabled(!Cfg.TextOutline))
                        changed |= SliderRow("Intensité du contour", null, Cfg.OutlineStrength, 0.1f, 1f, "%.2f", v => Cfg.OutlineStrength = v);
                });
                Card(() =>
                {
                    CardLabel("Fenêtre de réglages");
                    changed |= ToggleRow("Fond animé", "Halos lumineux et particules derrière les réglages.", Cfg.AnimatedSettingsBackground, v => Cfg.AnimatedSettingsBackground = v);
                });
                if (FancyButton("##resetColors", "Couleurs par défaut", FontAwesomeIcon.Undo, ButtonSize("Couleurs par défaut", FontAwesomeIcon.Undo), primary: false))
                {
                    var defaults = new Configuration();
                    Cfg.AccentColor = defaults.AccentColor;
                    Cfg.SecondaryColor = defaults.SecondaryColor;
                    Cfg.UseJobColors = defaults.UseJobColors;
                    Cfg.BarOpacity = defaults.BarOpacity;
                    changed = true;
                }
                break;
        }

        if (changed)
            Cfg.Save();
    }

    // =================================================================== Mesure

    private void DrawMeasurePage()
    {
        Hero("Mesure", "Ce qui est compté, et quand un combat s'arrête.");

        var changed = false;

        SectionTitle("Qui afficher");
        var half = (pageWidth - 12f * S) / 2f;
        changed |= ScopeCard("##scopeSelf", FontAwesomeIcon.User, "Moi seul", "Toi et tes familiers", MeterScope.Self, half);
        ImGui.SameLine(0, 12f * S);
        changed |= ScopeCard("##scopeParty", FontAwesomeIcon.Users, "Mon groupe", "Toi et les membres de ton groupe", MeterScope.Party, half);
        changed |= ScopeCard("##scopeAlliance", FontAwesomeIcon.UsersCog, "Mon alliance", "Tous les groupes d'un raid en alliance", MeterScope.Alliance, half);
        ImGui.SameLine(0, 12f * S);
        changed |= ScopeCard("##scopeAll", FontAwesomeIcon.Globe, "Tous les joueurs", "Tout le monde autour de toi (ALEA, chasses)", MeterScope.AllPlayers, half);
        ImGui.Dummy(new Vector2(0, 4f * S));

        SectionTitle("Calcul");
        Card(() =>
        {
            changed |= ToggleRow("Un seul combat par instance", "Le compteur tourne du premier coup jusqu'à la sortie de l'instance, sans se couper. Il repart de zéro à chaque wipe. Désactivé : un combat par groupe d'ennemis.", Cfg.KeepEncounterInDuty, v => Cfg.KeepEncounterInDuty = v);
            changed |= ToggleRow("Ajouter les familiers au joueur", "Fée, carbuncle, reine automate... comptent pour leur maître.", Cfg.MergePets, v => Cfg.MergePets = v);
            changed |= ToggleRow("Transcendance sur sa propre ligne", "Sinon, ses dégâts comptent pour le joueur qui la lance.", Cfg.SeparateLimitBreak, v => Cfg.SeparateLimitBreak = v);
            changed |= SliderIntRow("Fin de combat", "Secondes sans dégâts, une fois sorti de combat, avant de clore le combat.", Cfg.EndDelaySeconds, 1, 30, v => Cfg.EndDelaySeconds = v);
            changed |= SliderIntRow("Fenêtre du DPS récent", "En secondes.", Cfg.RecentWindowSeconds, 3, Combatant.MaxRecentWindow, v => Cfg.RecentWindowSeconds = v);
        });

        SectionTitle("Historique");
        Card(() =>
        {
            changed |= ToggleRow("Enregistrer les combats", "Sur ton PC, sans limite de temps. Seule la page Historique les supprime.", Cfg.SaveEncounters, v => Cfg.SaveEncounters = v);
            using (ImRaii.Disabled(!Cfg.SaveEncounters))
                changed |= SliderIntRow("Durée minimale", "En secondes. Les combats plus courts ne sont pas enregistrés.", Cfg.MinSaveSeconds, 0, 120, v => Cfg.MinSaveSeconds = v);
            changed |= ToggleRow("Résumé dans le chat", "Message en fin de combat. Il n'est visible que par toi.", Cfg.ChatSummary, v => Cfg.ChatSummary = v);
            changed |= ToggleRow("Annoncer les nouveaux records", "Message dans le chat quand tu bats ton record. Il n'est visible que par toi.", Cfg.ChatRecords, v => Cfg.ChatRecords = v);
        });

        SectionTitle("Précision des DoT");
        Card(() =>
        {
            TextDimmed("Le jeu annonce le montant de chaque tick de dégâts sur la durée, mais pas toujours qui l'a causé. Quand plusieurs joueurs sont possibles, le tick est partagé selon la puissance de leurs effets, comme le font les autres outils de mesure. Le total du groupe reste exact, la part de chacun est une estimation : l'infobulle d'un joueur indique combien de ses dégâts sont estimés.");
        });

        if (changed)
            Cfg.Save();
    }

    private bool ScopeCard(string id, FontAwesomeIcon icon, string title, string subtitle, MeterScope scope, float width)
    {
        if (!OptionCard(id, icon, title, subtitle, Cfg.Scope == scope, width))
            return false;

        Cfg.Scope = scope;
        return true;
    }

    // =================================================================== À propos

    private void DrawAboutPage()
    {
        var version = Plugin.PluginInterface.Manifest.AssemblyVersion.ToString();
        Hero("À propos", "Tes dégâts et ceux de ton groupe, en direct.", $"Version {version}", "par Powerl3ss");

        Card(() =>
        {
            var start = ImGui.GetCursorPos();
            DrawLogo(64f * S);
            ImGui.SameLine(0, 16f * S);
            ImGui.BeginGroup();
            ImGui.SetCursorPosY(start.Y + 6f * S);
            using (heroFont.Push())
                ImGui.TextUnformatted("DPSmeter");
            TextDimmed("Un compteur de dégâts autonome : aucun autre logiciel n'est nécessaire. Le plugin écoute seulement ce que le jeu reçoit, il n'envoie rien.");
            ImGui.EndGroup();
        });

        Card(() =>
        {
            CardLabel("Commandes");
            CommandRow("/dps", "Affiche ou masque le HUD");
            CommandRow("/dps config", "Ouvre ces réglages");
            CommandRow("/dps end", "Termine le combat en cours");
            CommandRow("/dps reset", "Remet le HUD à zéro (les combats enregistrés sont conservés)");
            CommandRow("/dps debug", "Affiche les compteurs de diagnostic dans le chat");
        });

        Card(() =>
        {
            CardLabel("Astuces");
            TextDimmed("Survole une barre du HUD : détail du joueur et de ses meilleures actions.");
            TextDimmed("Clic droit sur le HUD : menu rapide (mesure affichée, terminer le combat, verrouiller...).");
            TextDimmed("Clic sur le titre du HUD : le replier ou le déplier.");
        });

        Card(() =>
        {
            CardLabel("Diagnostic");
            var parser = plugin.Parser;
            Row(() =>
            {
                ImGui.TextUnformatted("Lecture des dégâts");
            }, PillWidth(parser.HooksActive ? "Active" : "Inactive", dot: true), () =>
            {
                Pill(parser.HooksActive ? "Active" : "Inactive", parser.HooksActive ? Success : Danger, dot: true, centerInFrame: true);
            });
            if (parser.HookError != null)
                TextDimmed(parser.HookError);
            TextDimmed($"Actions lues : {Formatting.Full(parser.ActionPackets)}  |  coups : {Formatting.Full(parser.DamageEffects)}  |  ticks de DoT : {Formatting.Full(parser.DotTicks)}");
            TextDimmed($"Dégâts de DoT sans auteur trouvé : {Formatting.Full(parser.UnattributedDotDamage)}");
            TextDimmed($"Soins lus : {Formatting.Full(parser.HealEffects)}  |  ticks de HoT : {Formatting.Full(parser.HotTicks)}  |  morts vues : {Formatting.Full(parser.Deaths)}");
        });

        if (FancyButton("##repo", "Ouvrir la page GitHub", FontAwesomeIcon.ExternalLinkAlt, ButtonSize("Ouvrir la page GitHub", FontAwesomeIcon.ExternalLinkAlt), primary: false))
            Util.OpenLink(RepoUrl);
    }

    private void CommandRow(string command, string description)
    {
        Row(() =>
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Accent))
                ImGui.TextUnformatted(command);
            ImGui.SameLine(0, 16f * S);
            ImGui.TextColored(TextDim, description);
        }, 0f, null);
    }
}
