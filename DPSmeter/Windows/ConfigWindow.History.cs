using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using DPSmeter.Core;

namespace DPSmeter.Windows;

/// <summary>Page Historique : une entrée par passage dans une instance, avec ses wipes, ses filtres, son tri et sa suppression.</summary>
public sealed partial class ConfigWindow
{
    // Dessiner des milliers de cartes à chaque image ralentirait le jeu : la liste s'affiche par tranches.
    private const int HistoryPageSize = 40;

    private string filterCategory = string.Empty;
    private string filterPlace = string.Empty;
    private string filterCharacter = string.Empty;
    private string filterJob = string.Empty;
    private string filterOutcome = string.Empty;
    private string? scrollToRun;
    private EncounterSort historySort = EncounterSort.Newest;

    private List<EncounterRun> historyList = new();
    private int historyRunCount;
    private List<(string Value, int Count)> categoryOptions = new();
    private List<(string Value, int Count)> placeOptions = new();
    private List<(string Value, int Count)> characterOptions = new();
    private List<(string Value, int Count)> jobOptions = new();
    private List<(string Value, int Count)> outcomeOptions = new();
    private readonly Dictionary<string, uint> jobIds = new();
    private int historyVersion = -1;
    private bool historyDirty = true;
    private int historyShown = HistoryPageSize;

    private readonly HashSet<string> selectedRuns = new();
    private string? expandedRun;
    private string? expandedAttempt;

    private void DrawHistoryPage()
    {
        var store = plugin.Store;
        RefreshHistoryList();
        RefreshBests();

        Hero("Historique", "Tes combats enregistrés, conservés d'une session à l'autre.",
             historyRunCount > 0 ? $"{historyRunCount} entrée(s)" : null);

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
        string[]? toDelete = null;
        for (var i = 0; i < count; i++)
        {
            var run = historyList[i];
            if (DrawRunCard(run))
                toDelete = run.Attempts.Select(a => a.Id).ToArray();

            if (run.Id == expandedRun)
            {
                ImGui.Dummy(new Vector2(0, 2f * S));
                if (run.Attempts.Count == 1)
                {
                    DrawEncounterDetails(run.First.View);
                }
                else
                {
                    for (var a = 0; a < run.Attempts.Count; a++)
                    {
                        var attempt = run.Attempts[a];
                        if (DrawAttemptRow(attempt, a))
                            toDelete = new[] { attempt.Id };

                        if (attempt.Id == expandedAttempt)
                        {
                            ImGui.Dummy(new Vector2(0, 2f * S));
                            DrawEncounterDetails(attempt.View);
                        }
                        ImGui.Dummy(new Vector2(0, 1f * S));
                    }
                }
            }
            ImGui.Dummy(new Vector2(0, 2f * S));
        }

        if (toDelete != null)
            DeleteRecords(toDelete);

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
        var runs = EncounterQuery.Group(all);
        historyRunCount = runs.Count;

        categoryOptions = EncounterQuery.Options(runs, r => r.Category);
        characterOptions = EncounterQuery.Options(runs, r => r.CharacterName);

        jobIds.Clear();
        foreach (var run in runs)
        {
            if (run.LocalJobId != 0)
                jobIds[plugin.JobAbbreviation(run.LocalJobId)] = run.LocalJobId;
        }
        jobOptions = EncounterQuery.Options(runs, r => r.LocalJobId != 0 ? plugin.JobAbbreviation(r.LocalJobId) : string.Empty);
        outcomeOptions = EncounterQuery.Options(runs, r => Formatting.OutcomeLabel(r.Outcome));

        // Un filtre qui ne correspond plus à rien (derniers combats supprimés) revient à « tous ».
        if (!categoryOptions.Any(o => o.Value == filterCategory))
            filterCategory = string.Empty;
        if (!characterOptions.Any(o => o.Value == filterCharacter))
            filterCharacter = string.Empty;
        if (!jobOptions.Any(o => o.Value == filterJob))
            filterJob = string.Empty;
        if (!outcomeOptions.Any(o => o.Value == filterOutcome))
            filterOutcome = string.Empty;

        // Les lieux proposés dépendent du type choisi.
        placeOptions = EncounterQuery.Options(runs.Where(r => filterCategory.Length == 0 || r.Category == filterCategory), r => r.Place);
        if (!placeOptions.Any(o => o.Value == filterPlace))
            filterPlace = string.Empty;

        var filter = new EncounterFilter(
            filterCategory, filterPlace, filterCharacter,
            jobIds.GetValueOrDefault(filterJob),
            Enum.GetValues<Outcome>().Where(o => o != Outcome.Unknown && Formatting.OutcomeLabel(o) == filterOutcome).Select(o => (Outcome?)o).FirstOrDefault());
        historyList = EncounterQuery.Sort(runs.Where(filter.Matches), historySort);

        // La sélection et le détail ouvert ne survivent pas à la disparition de leur entrée.
        var ids = runs.Select(r => r.Id).ToHashSet();
        selectedRuns.IntersectWith(ids);
        if (expandedRun != null && !ids.Contains(expandedRun))
            expandedRun = null;
        if (expandedAttempt != null && !all.Any(r => r.Id == expandedAttempt))
            expandedAttempt = null;
    }

    private void DrawHistoryFilters()
    {
        Card(() =>
        {
            const int Columns = 3;
            var gap = 12f * S;
            var width = (cardInner - gap * (Columns - 1)) / Columns;
            var rowHeight = ImGui.GetTextLineHeightWithSpacing() + FrameH + gap;

            // Colonnes placées à la main : ImGui.SameLine décalerait vers le bas le texte de chaque
            // colonne suivante, pour l'aligner sur le texte de la liste déroulante d'à côté.
            var start = ImGui.GetCursorPos();
            var cell = 0;
            void NextCell()
            {
                ImGui.SetCursorPos(new Vector2(start.X + cell % Columns * (width + gap), start.Y + (float)(cell / Columns) * rowHeight));
                cell++;
            }

            NextCell();
            if (FilterCombo("Type", "##filterType", "Tous les types", filterCategory, categoryOptions, width, out var category))
            {
                filterCategory = category;
                filterPlace = string.Empty;
                OnFiltersChanged();
            }

            NextCell();
            if (FilterCombo("Lieu", "##filterPlace", "Tous les lieux", filterPlace, placeOptions, width, out var place))
            {
                filterPlace = place;
                OnFiltersChanged();
            }

            NextCell();
            if (FilterCombo("Job", "##filterJob", "Tous les jobs", filterJob, jobOptions, width, out var job))
            {
                filterJob = job;
                OnFiltersChanged();
            }

            NextCell();
            if (FilterCombo("Issue", "##filterOutcome", "Toutes", filterOutcome, outcomeOptions, width, out var outcome))
            {
                filterOutcome = outcome;
                OnFiltersChanged();
            }

            if (characterOptions.Count > 1)
            {
                NextCell();
                if (FilterCombo("Personnage", "##filterCharacter", "Tous", filterCharacter, characterOptions, width, out var character))
                {
                    filterCharacter = character;
                    OnFiltersChanged();
                }
            }

            NextCell();
            DrawSortCombo(width);
        });
    }

    private void OnFiltersChanged()
    {
        historyDirty = true;
        historyShown = HistoryPageSize;

        // Jamais de combat sélectionné hors de la vue : on ne supprime que ce qu'on voit.
        selectedRuns.Clear();
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
        var allSelected = historyList.Count > 0 && historyList.All(r => selectedRuns.Contains(r.Id));
        var selectLabel = allSelected ? "Tout désélectionner" : "Tout sélectionner";
        if (historyList.Count > 0 &&
            FancyButton("##historySelect", selectLabel, FontAwesomeIcon.CheckDouble, ButtonSize(selectLabel, FontAwesomeIcon.CheckDouble), primary: false))
        {
            if (allSelected)
                selectedRuns.ExceptWith(historyList.Select(r => r.Id));
            else
                selectedRuns.UnionWith(historyList.Select(r => r.Id));
        }
        if (historyList.Count > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip("Sélectionne les entrées de la liste, filtres compris");

        if (selectedRuns.Count > 0)
        {
            ImGui.SameLine();
            var deleteLabel = $"Supprimer la sélection ({selectedRuns.Count})";
            if (FancyButton("##historyDelete", deleteLabel, FontAwesomeIcon.Trash, ButtonSize(deleteLabel, FontAwesomeIcon.Trash), primary: false, danger: true) &&
                ImGui.GetIO().KeyCtrl)
            {
                DeleteRecords(historyList.Where(r => selectedRuns.Contains(r.Id)).SelectMany(r => r.Attempts).Select(a => a.Id).ToArray());
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Ctrl + clic : supprime définitivement les entrées sélectionnées, avec tous leurs wipes");
        }

        ImGui.Dummy(new Vector2(0, 2f * S));
    }

    private void DeleteRecords(IReadOnlyCollection<string> ids)
    {
        plugin.Store.Delete(ids);
        RefreshHistoryList();
    }

    private Vector4 OutcomeColor(Outcome outcome) => outcome switch
    {
        Outcome.Victory => Success,
        Outcome.Failure => Danger,
        _ => TextDim,
    };

    /// <summary>Une entrée de la liste : case de sélection, résumé cliquable, corbeille. Renvoie vrai pour la supprimer.</summary>
    private bool DrawRunCard(EncounterRun run)
    {
        using var id = ImRaii.PushId(run.Id);

        var lineH = ImGui.GetTextLineHeight();
        var height = lineH * 2 + 26f * S;
        var pad = 12f * S;
        var checkZone = pad + FrameH + 8f * S;
        var trashZone = FrameH + pad + 8f * S;

        if (scrollToRun == run.Id)
        {
            ImGui.SetScrollHereY(0.1f);
            scrollToRun = null;
        }

        var startLocal = ImGui.GetCursorPos();
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(pageWidth, height);
        var isExpanded = run.Id == expandedRun;
        var isSelected = selectedRuns.Contains(run.Id);
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
                selectedRuns.Remove(run.Id);
            else
                selectedRuns.Add(run.Id);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(isSelected ? "Retirer de la sélection" : "Ajouter à la sélection");

        // Résumé : un clic ouvre ou ferme le détail.
        var bodyWidth = pageWidth - checkZone - trashZone;
        ImGui.SetCursorPos(startLocal + new Vector2(checkZone, 0));
        if (ImGui.InvisibleButton("##open", new Vector2(bodyWidth, height)))
        {
            expandedRun = isExpanded ? null : run.Id;
            expandedAttempt = null;
        }

        var textMin = min + new Vector2(checkZone, 0);
        var textMax = textMin + new Vector2(bodyWidth, height);
        var top = min.Y + (height - lineH * 2 - 4f * S) / 2f;
        var date = run.StartUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        var dateWidth = ImGui.CalcTextSize(date).X;

        drawList.AddText(new Vector2(textMax.X - dateWidth, top), Col(TextDim), date);
        drawList.PushClipRect(textMin, new Vector2(textMax.X - dateWidth - pad, textMax.Y), true);
        var titleX = textMin.X;
        if (run.Attempts.Any(a => bestRecordIds.Contains(a.Id)))
        {
            var trophy = FontAwesomeIcon.Trophy.ToIconString();
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                drawList.AddText(new Vector2(titleX, top), Col(SunColor), trophy);
                titleX += ImGui.CalcTextSize(trophy).X + 6f * S;
            }
        }
        drawList.AddText(new Vector2(titleX, top), Col(TextMain), run.Title);
        drawList.PopClipRect();

        // Du plus utile au moins utile : si la place manque, c'est la fin qui est coupée.
        var details = $"{Formatting.Duration(run.DurationSeconds)}  |  {Formatting.Compact(run.TotalDamage)} dégâts";
        if (run.Attempts.Count > 1)
            details += $"  |  {run.Attempts.Count} essais";
        else
            details += $"  |  groupe {Formatting.Compact(run.First.View.TotalDps)}";
        if (run.BestLocalDps > 0)
        {
            var job = plugin.JobAbbreviation(run.LocalJobId);
            var label = run.Attempts.Count > 1 ? "ton meilleur" : "toi";
            details += job.Length > 0 ? $"  |  {label} {Formatting.Compact(run.BestLocalDps)} ({job})" : $"  |  {label} {Formatting.Compact(run.BestLocalDps)}";
        }
        details += $"  |  {run.Category}";
        if (run.Place != run.Title)
            details += $"  |  {run.Place}";

        var secondLine = top + lineH + 4f * S;
        var outcomeText = Formatting.RunOutcomeText(run);
        var outcomeWidth = outcomeText.Length > 0 ? ImGui.CalcTextSize(outcomeText).X : 0f;
        if (outcomeWidth > 0f)
            drawList.AddText(new Vector2(textMax.X - outcomeWidth, secondLine), Col(OutcomeColor(run.Outcome)), outcomeText);

        drawList.PushClipRect(textMin, new Vector2(textMax.X - outcomeWidth - pad, textMax.Y), true);
        drawList.AddText(new Vector2(textMin.X, secondLine), Col(TextDim), details);
        drawList.PopClipRect();

        // Corbeille.
        ImGui.SetCursorPos(startLocal + new Vector2(pageWidth - pad - FrameH, (height - FrameH) / 2f));
        var delete = IconButton("##delete", FontAwesomeIcon.Trash, "Ctrl + clic : supprime définitivement cette entrée, avec tous ses wipes", hoverColor: Danger) &&
                     ImGui.GetIO().KeyCtrl;

        ImGui.SetCursorPos(new Vector2(startLocal.X, startLocal.Y + height));
        ImGui.Dummy(new Vector2(pageWidth, 0));
        return delete;
    }

    /// <summary>Un essai à l'intérieur d'une entrée : wipe numéroté ou essai final. Renvoie vrai pour le supprimer.</summary>
    private bool DrawAttemptRow(EncounterRecord attempt, int index)
    {
        using var id = ImRaii.PushId(attempt.Id);

        var view = attempt.View;
        var lineH = ImGui.GetTextLineHeight();
        var height = lineH + 18f * S;
        var indent = 28f * S;
        var pad = 10f * S;
        var width = pageWidth - indent;
        var trashZone = FrameH + pad + 4f * S;

        var startLocal = ImGui.GetCursorPos() + new Vector2(indent, 0);
        var min = ImGui.GetCursorScreenPos() + new Vector2(indent, 0);
        var max = min + new Vector2(width, height);
        var isExpanded = attempt.Id == expandedAttempt;
        var drawList = ImGui.GetWindowDrawList();

        var hovered = ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(min, max);
        drawList.AddRectFilled(min, max, Col(isExpanded ? AccentSoft : hovered ? CardBgHover : CardBg), 8f * S);
        drawList.AddRect(min, max, Col(isExpanded ? AccentBorder : Line), 8f * S);

        ImGui.SetCursorPos(startLocal);
        if (ImGui.InvisibleButton("##openAttempt", new Vector2(width - trashZone, height)))
            expandedAttempt = isExpanded ? null : attempt.Id;

        // Étiquette à gauche : « Wipe 2 », « Victoire », ou l'adversaire d'un combat ordinaire.
        var y = min.Y + (height - lineH) / 2f;
        var labelWidth = 200f * S;
        var label = Formatting.RecordOutcomeText(attempt);
        if (label.Length == 0)
            label = $"Combat {index + 1}";
        drawList.PushClipRect(min, new Vector2(min.X + pad + labelWidth, max.Y), true);
        drawList.AddText(new Vector2(min.X + pad, y), Col(OutcomeColor(view.Outcome)), label);
        drawList.PopClipRect();

        var summary = $"{view.StartUtc.ToLocalTime():HH:mm}  |  {Formatting.Duration(view.DurationSeconds)}  |  {Formatting.Compact(view.TotalDamage)} dégâts  |  groupe {Formatting.Compact(view.TotalDps)}";
        if (view.Local is { } local)
            summary += $"  |  toi {Formatting.Compact(local.Dps)}";
        drawList.PushClipRect(min, new Vector2(max.X - trashZone, max.Y), true);
        drawList.AddText(new Vector2(min.X + pad + labelWidth + 8f * S, y), Col(TextDim), summary);
        drawList.PopClipRect();

        ImGui.SetCursorPos(startLocal + new Vector2(width - pad - FrameH, (height - FrameH) / 2f));
        var delete = IconButton("##deleteAttempt", FontAwesomeIcon.Trash, "Ctrl + clic : supprime définitivement cet essai", hoverColor: Danger) &&
                     ImGui.GetIO().KeyCtrl;

        ImGui.SetCursorPos(new Vector2(startLocal.X - indent, startLocal.Y + height));
        ImGui.Dummy(new Vector2(pageWidth, 0));
        return delete;
    }
}
