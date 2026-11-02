using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using DPSmeter.Core;

namespace DPSmeter.Windows;

/// <summary>Page Historique : les combats enregistrés, avec filtres, tri et suppression.</summary>
public sealed partial class ConfigWindow
{
    // Dessiner des milliers de cartes à chaque image ralentirait le jeu : la liste s'affiche par tranches.
    private const int HistoryPageSize = 40;

    private string filterCategory = string.Empty;
    private string filterPlace = string.Empty;
    private string filterCharacter = string.Empty;
    private EncounterSort historySort = EncounterSort.Newest;

    private List<EncounterRecord> historyList = new();
    private List<(string Value, int Count)> categoryOptions = new();
    private List<(string Value, int Count)> placeOptions = new();
    private List<(string Value, int Count)> characterOptions = new();
    private int historyVersion = -1;
    private bool historyDirty = true;
    private int historyShown = HistoryPageSize;

    private readonly HashSet<string> selectedRecords = new();
    private string? expandedRecord;

    private void DrawHistoryPage()
    {
        var store = plugin.Store;
        RefreshHistoryList();

        Hero("Historique", "Tes combats enregistrés, conservés d'une session à l'autre.",
             store.Count > 0 ? $"{store.Count} combat(s)" : null);

        if (!store.IsLoaded)
        {
            Card(() =>
            {
                IconText(FontAwesomeIcon.Spinner, TextDim);
                ImGui.SameLine();
                ImGui.TextUnformatted("Chargement des combats...");
            });
            return;
        }

        if (store.UnreadableFiles > 0)
        {
            Card(() =>
            {
                IconText(FontAwesomeIcon.ExclamationTriangle, Danger);
                ImGui.SameLine();
                ImGui.TextUnformatted($"{store.UnreadableFiles} fichier(s) de combat illisible(s), laissés de côté.");
            });
        }

        if (store.Count == 0)
        {
            Card(() =>
            {
                IconText(FontAwesomeIcon.History, TextDim);
                ImGui.SameLine();
                ImGui.TextUnformatted("Aucun combat enregistré pour l'instant.");
                TextDimmed(Cfg.SaveEncounters
                    ? $"Les combats d'au moins {Cfg.MinSaveSeconds} secondes sont enregistrés automatiquement à leur fin."
                    : "L'enregistrement est désactivé dans l'onglet Mesure.");
            });
            return;
        }

        DrawHistoryFilters();
        DrawHistoryActions();

        if (historyList.Count == 0)
        {
            Card(() => ImGui.TextUnformatted("Aucun combat ne correspond à ces filtres."));
            return;
        }

        var count = Math.Min(historyShown, historyList.Count);
        EncounterRecord? toDelete = null;
        for (var i = 0; i < count; i++)
        {
            var record = historyList[i];
            if (DrawEncounterCard(record))
                toDelete = record;

            if (record.Id == expandedRecord)
            {
                ImGui.Dummy(new Vector2(0, 2f * S));
                DrawCombatantTable(record.View);
            }
            ImGui.Dummy(new Vector2(0, 2f * S));
        }

        if (toDelete != null)
            DeleteRecords(new[] { toDelete.Id });

        if (historyList.Count > count)
        {
            var label = $"Afficher plus ({historyList.Count - count} restant(s))";
            if (FancyButton("##historyMore", label, FontAwesomeIcon.ChevronDown, ButtonSize(label, FontAwesomeIcon.ChevronDown), primary: false))
                historyShown += HistoryPageSize;
        }
    }

    /// <summary>Recalcule la liste seulement si les combats ou les filtres ont changé.</summary>
    private void RefreshHistoryList()
    {
        var version = plugin.Store.Version;
        if (!historyDirty && version == historyVersion)
            return;

        historyVersion = version;
        historyDirty = false;

        var all = plugin.Store.All();
        categoryOptions = EncounterQuery.Options(all, r => r.Category);
        characterOptions = EncounterQuery.Options(all, r => r.CharacterName);

        // Un filtre qui ne correspond plus à rien (derniers combats supprimés) revient à « tous ».
        if (!categoryOptions.Any(o => o.Value == filterCategory))
            filterCategory = string.Empty;
        if (!characterOptions.Any(o => o.Value == filterCharacter))
            filterCharacter = string.Empty;

        // Les lieux proposés dépendent du type choisi.
        placeOptions = EncounterQuery.Options(all.Where(r => filterCategory.Length == 0 || r.Category == filterCategory), r => r.Place);
        if (!placeOptions.Any(o => o.Value == filterPlace))
            filterPlace = string.Empty;

        historyList = EncounterQuery.Apply(all, filterCategory, filterPlace, filterCharacter, historySort);

        // La sélection et le détail ouvert ne survivent pas à la disparition de leur combat.
        var ids = all.Select(r => r.Id).ToHashSet();
        selectedRecords.IntersectWith(ids);
        if (expandedRecord != null && !ids.Contains(expandedRecord))
            expandedRecord = null;
    }

    private void DrawHistoryFilters()
    {
        Card(() =>
        {
            var showCharacters = characterOptions.Count > 1;
            var gap = 12f * S;
            var columns = showCharacters ? 4 : 3;
            var width = (cardInner - gap * (columns - 1)) / columns;

            if (FilterCombo("Type", "##filterType", "Tous les types", filterCategory, categoryOptions, width, out var category))
            {
                filterCategory = category;
                filterPlace = string.Empty;
                OnFiltersChanged();
            }

            ImGui.SameLine(0, gap);
            if (FilterCombo("Lieu", "##filterPlace", "Tous les lieux", filterPlace, placeOptions, width, out var place))
            {
                filterPlace = place;
                OnFiltersChanged();
            }

            if (showCharacters)
            {
                ImGui.SameLine(0, gap);
                if (FilterCombo("Personnage", "##filterCharacter", "Tous", filterCharacter, characterOptions, width, out var character))
                {
                    filterCharacter = character;
                    OnFiltersChanged();
                }
            }

            ImGui.SameLine(0, gap);
            DrawSortCombo(width);
        });
    }

    private void OnFiltersChanged()
    {
        historyDirty = true;
        historyShown = HistoryPageSize;

        // Jamais de combat sélectionné hors de la vue : on ne supprime que ce qu'on voit.
        selectedRecords.Clear();
    }

    private static bool FilterCombo(string label, string id, string allLabel, string current,
                                    List<(string Value, int Count)> options, float width, out string chosen)
    {
        chosen = current;
        var changed = false;

        ImGui.BeginGroup();
        ImGui.TextColored(TextDim, label);
        ImGui.SetNextItemWidth(width);
        using (var combo = ImRaii.Combo(id, current.Length == 0 ? allLabel : current))
        {
            if (combo.Success)
            {
                if (ImGui.Selectable(allLabel, current.Length == 0))
                {
                    chosen = string.Empty;
                    changed = true;
                }

                foreach (var (value, count) in options)
                {
                    if (ImGui.Selectable($"{value} ({count})", value == current))
                    {
                        chosen = value;
                        changed = true;
                    }
                }
            }
        }
        ImGui.EndGroup();
        return changed;
    }

    private void DrawSortCombo(float width)
    {
        ImGui.BeginGroup();
        ImGui.TextColored(TextDim, "Trier par");
        ImGui.SetNextItemWidth(width);
        using (var combo = ImRaii.Combo("##historySort", EncounterQuery.SortLabel(historySort)))
        {
            if (combo.Success)
            {
                foreach (var sort in Enum.GetValues<EncounterSort>())
                {
                    if (ImGui.Selectable(EncounterQuery.SortLabel(sort), sort == historySort))
                    {
                        historySort = sort;
                        historyDirty = true;
                    }
                }
            }
        }
        ImGui.EndGroup();
    }

    private void DrawHistoryActions()
    {
        var allSelected = historyList.Count > 0 && historyList.All(r => selectedRecords.Contains(r.Id));
        var selectLabel = allSelected ? "Tout désélectionner" : "Tout sélectionner";
        if (historyList.Count > 0 &&
            FancyButton("##historySelect", selectLabel, FontAwesomeIcon.CheckDouble, ButtonSize(selectLabel, FontAwesomeIcon.CheckDouble), primary: false))
        {
            if (allSelected)
                selectedRecords.ExceptWith(historyList.Select(r => r.Id));
            else
                selectedRecords.UnionWith(historyList.Select(r => r.Id));
        }
        if (historyList.Count > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip("Sélectionne les combats de la liste, filtres compris");

        if (selectedRecords.Count > 0)
        {
            ImGui.SameLine();
            var deleteLabel = $"Supprimer la sélection ({selectedRecords.Count})";
            if (FancyButton("##historyDelete", deleteLabel, FontAwesomeIcon.Trash, ButtonSize(deleteLabel, FontAwesomeIcon.Trash), primary: false, danger: true) &&
                ImGui.GetIO().KeyCtrl)
                DeleteRecords(selectedRecords.ToArray());
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Ctrl + clic : supprime définitivement les combats sélectionnés");
        }

        ImGui.Dummy(new Vector2(0, 2f * S));
    }

    private void DeleteRecords(IReadOnlyCollection<string> ids)
    {
        plugin.Store.Delete(ids);
        selectedRecords.ExceptWith(ids);
        RefreshHistoryList();
    }

    /// <summary>Une ligne de la liste : case de sélection, résumé cliquable, corbeille. Renvoie vrai pour supprimer.</summary>
    private bool DrawEncounterCard(EncounterRecord record)
    {
        using var id = ImRaii.PushId(record.Id);

        var view = record.View;
        var lineH = ImGui.GetTextLineHeight();
        var height = lineH * 2 + 26f * S;
        var pad = 12f * S;
        var checkZone = pad + FrameH + 8f * S;
        var trashZone = FrameH + pad + 8f * S;

        var startLocal = ImGui.GetCursorPos();
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(pageWidth, height);
        var isExpanded = record.Id == expandedRecord;
        var isSelected = selectedRecords.Contains(record.Id);
        var drawList = ImGui.GetWindowDrawList();

        // Le fond d'abord : les boutons sont invisibles, le texte se dessine par-dessus.
        var hovered = ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(min, max);
        drawList.AddRectFilled(min, max, Col(isExpanded ? AccentSoft : hovered ? CardBgHover : CardBg), 10f * S);
        drawList.AddRect(min, max, Col(isExpanded ? AccentBorder : Line), 10f * S);

        // Case de sélection.
        ImGui.SetCursorPos(startLocal + new Vector2(pad, (height - FrameH) / 2f));
        if (CheckCircle("##select", isSelected))
        {
            if (isSelected)
                selectedRecords.Remove(record.Id);
            else
                selectedRecords.Add(record.Id);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(isSelected ? "Retirer de la sélection" : "Ajouter à la sélection");

        // Résumé : un clic ouvre ou ferme le détail du combat.
        var bodyWidth = pageWidth - checkZone - trashZone;
        ImGui.SetCursorPos(startLocal + new Vector2(checkZone, 0));
        if (ImGui.InvisibleButton("##open", new Vector2(bodyWidth, height)))
            expandedRecord = isExpanded ? null : record.Id;

        var textMin = min + new Vector2(checkZone, 0);
        var textMax = textMin + new Vector2(bodyWidth, height);
        var top = min.Y + (height - lineH * 2 - 4f * S) / 2f;
        var date = view.StartUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        var dateWidth = ImGui.CalcTextSize(date).X;

        drawList.AddText(new Vector2(textMax.X - dateWidth, top), Col(TextDim), date);
        drawList.PushClipRect(textMin, new Vector2(textMax.X - dateWidth - pad, textMax.Y), true);
        drawList.AddText(new Vector2(textMin.X, top), Col(TextMain), view.Title);
        drawList.PopClipRect();

        var details = $"{record.Category}  |  {record.Place}  |  {Formatting.Duration(view.DurationSeconds)}  |  groupe {Formatting.Compact(view.TotalDps)}";
        if (view.Local is { } local)
        {
            var job = plugin.JobAbbreviation(local.JobId);
            details += job.Length > 0 ? $"  |  toi {Formatting.Compact(local.Dps)} ({job})" : $"  |  toi {Formatting.Compact(local.Dps)}";
        }
        drawList.PushClipRect(textMin, textMax, true);
        drawList.AddText(new Vector2(textMin.X, top + lineH + 4f * S), Col(TextDim), details);
        drawList.PopClipRect();

        // Corbeille.
        ImGui.SetCursorPos(startLocal + new Vector2(pageWidth - pad - FrameH, (height - FrameH) / 2f));
        var delete = IconButton("##delete", FontAwesomeIcon.Trash, "Ctrl + clic : supprime définitivement ce combat", hoverColor: Danger) &&
                     ImGui.GetIO().KeyCtrl;

        ImGui.SetCursorPos(new Vector2(startLocal.X, startLocal.Y + height));
        ImGui.Dummy(new Vector2(pageWidth, 0));
        return delete;
    }
}
