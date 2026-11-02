using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using DPSmeter.Core;

namespace DPSmeter.Windows;

/// <summary>Page Records : ton meilleur DPS par adversaire, job et personnage, avec ta progression.</summary>
public sealed partial class ConfigWindow
{
    private List<PersonalBest> personalBests = new();
    private HashSet<string> bestRecordIds = new();
    private int bestsVersion = -1;

    private string recordsJob = string.Empty;
    private string recordsCharacter = string.Empty;
    private int recordsShown = HistoryPageSize;

    private void RefreshBests()
    {
        var version = plugin.Store.Version;
        if (version == bestsVersion)
            return;

        bestsVersion = version;
        personalBests = PersonalBests.Compute(plugin.Store.All());
        bestRecordIds = personalBests.Select(b => b.BestRecordId).ToHashSet();
    }

    /// <summary>Ton record sur le combat affiché, pour le comparer à ton DPS sur la page d'accueil.</summary>
    private PersonalBest? BestFor(EncounterView view)
    {
        if (view.Local is not { } local)
            return null;

        RefreshBests();
        return personalBests.FirstOrDefault(b => b.Title == view.Title && b.Place == view.ZoneName && b.JobId == local.JobId && b.CharacterName == local.Name);
    }

    private void DrawRecordsPage()
    {
        RefreshBests();
        Hero("Records", "Ton meilleur DPS par instance ou adversaire, job et personnage.",
             personalBests.Count > 0 ? $"{personalBests.Count} record(s)" : null);

        if (!plugin.Store.IsLoaded)
        {
            Card(() =>
            {
                IconText(FontAwesomeIcon.Spinner, TextDim);
                ImGui.SameLine();
                ImGui.TextUnformatted("Chargement des combats...");
            });
            return;
        }

        if (personalBests.Count == 0)
        {
            Card(() =>
            {
                IconText(FontAwesomeIcon.Trophy, TextDim);
                ImGui.SameLine();
                ImGui.TextUnformatted("Aucun record pour l'instant.");
                TextDimmed(RecordRules);
            });
            return;
        }

        var jobs = personalBests.GroupBy(b => plugin.JobAbbreviation(b.JobId))
                                .Where(g => g.Key.Length > 0)
                                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                                .Select(g => (g.Key, g.Count()))
                                .ToList();
        var characters = personalBests.GroupBy(b => b.CharacterName)
                                      .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                                      .Select(g => (g.Key, g.Count()))
                                      .ToList();

        // Un filtre qui ne correspond plus à rien (combats supprimés) revient à « tous ».
        if (!jobs.Any(j => j.Key == recordsJob))
            recordsJob = string.Empty;
        if (!characters.Any(c => c.Key == recordsCharacter))
            recordsCharacter = string.Empty;

        Card(() =>
        {
            var gap = 12f * S;
            var width = (cardInner - gap) / 2f;
            var start = ImGui.GetCursorPos();

            if (FilterCombo("Job", "##recordsJob", "Tous les jobs", recordsJob, jobs, width, out var job))
            {
                recordsJob = job;
                recordsShown = HistoryPageSize;
            }

            if (characters.Count > 1)
            {
                ImGui.SetCursorPos(new Vector2(start.X + width + gap, start.Y));
                if (FilterCombo("Personnage", "##recordsCharacter", "Tous", recordsCharacter, characters, width, out var character))
                {
                    recordsCharacter = character;
                    recordsShown = HistoryPageSize;
                }
            }

            TextDimmed(RecordRules);
        });

        var shown = personalBests
            .Where(b => (recordsJob.Length == 0 || plugin.JobAbbreviation(b.JobId) == recordsJob) &&
                        (recordsCharacter.Length == 0 || b.CharacterName == recordsCharacter))
            .ToList();

        var count = Math.Min(recordsShown, shown.Count);
        for (var i = 0; i < count; i++)
        {
            DrawRecordCard(shown[i], i);
            ImGui.Dummy(new Vector2(0, 2f * S));
        }

        if (shown.Count > count)
        {
            var label = $"Afficher plus ({shown.Count - count} restant(s))";
            if (FancyButton("##recordsMore", label, FontAwesomeIcon.ChevronDown, ButtonSize(label, FontAwesomeIcon.ChevronDown), primary: false))
                recordsShown += HistoryPageSize;
        }
    }

    private static string RecordRules =>
        $"Comptent les combats d'au moins {PersonalBests.MinDurationSeconds:0} secondes qui ne se sont pas soldés par un échec.";

    private void DrawRecordCard(PersonalBest best, int index)
    {
        var lineH = ImGui.GetTextLineHeight();
        var height = lineH * 2 + 26f * S;
        var pad = 14f * S;
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(pageWidth, height);

        var clicked = ImGui.InvisibleButton($"##record{index}", new Vector2(pageWidth, height));
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(min, max, Col(hovered ? CardBgHover : CardBg), 10f * S);
        drawList.AddRect(min, max, Col(Line), 10f * S);

        // Couronne : le meilleur chiffre, en grand à droite.
        var bestText = $"{Formatting.Compact(best.BestDps)} DPS";
        var bestWidth = ImGui.CalcTextSize(bestText).X;
        var date = best.BestDateUtc.ToLocalTime().ToString("dd/MM/yyyy");
        var dateWidth = ImGui.CalcTextSize(date).X;
        var top = min.Y + (height - lineH * 2 - 4f * S) / 2f;
        var rightWidth = Math.Max(bestWidth, dateWidth);
        drawList.AddText(new Vector2(max.X - pad - bestWidth, top), Col(Accent), bestText);
        drawList.AddText(new Vector2(max.X - pad - dateWidth, top + lineH + 4f * S), Col(TextDim), date);

        // Progression au fil des essais, juste à gauche du record.
        var chartWidth = 110f * S;
        var chartMax = new Vector2(max.X - pad * 2f - rightWidth, max.Y - 12f * S);
        var chartMin = new Vector2(chartMax.X - chartWidth, min.Y + 12f * S);
        DrawProgress(best, chartMin, chartMax);

        // Adversaire et résumé à gauche, coupés avant la courbe.
        var textMin = min + new Vector2(pad, 0);
        drawList.PushClipRect(textMin, new Vector2(chartMin.X - pad, max.Y), true);
        drawList.AddText(new Vector2(textMin.X, top), Col(TextMain), best.Title);
        var job = plugin.JobAbbreviation(best.JobId);
        var attempts = best.Attempts > 1 ? $"{best.Attempts} essais" : "1 essai";
        var details = $"{job}  |  {best.Place}  |  {attempts}  |  dernier {Formatting.Compact(best.LastDps)}";
        drawList.AddText(new Vector2(textMin.X, top + lineH + 4f * S), Col(TextDim), details);
        drawList.PopClipRect();

        if (hovered)
            ImGui.SetTooltip($"{best.CharacterName}\nRecord : {Formatting.Full(best.BestDps)} DPS\nClic : ouvrir ce combat dans l'historique");

        if (clicked)
            OpenRecord(best.BestRecordId);
    }

    private void DrawProgress(PersonalBest best, Vector2 min, Vector2 max)
    {
        var values = best.Progress;
        if (values.Count < 2)
            return;

        var drawList = ImGui.GetWindowDrawList();
        var low = values.Min();
        var high = values.Max();
        var range = Math.Max(1.0, high - low);
        var size = max - min;

        Vector2 Point(int index) => new(
            min.X + size.X * index / (values.Count - 1),
            max.Y - (float)((values[index] - low) / range) * size.Y);

        for (var i = 0; i < values.Count; i++)
            drawList.PathLineTo(Point(i));
        drawList.PathStroke(Col(Accent with { W = 0.8f }), ImDrawFlags.None, 1.5f * S);

        // Le point du record, puis le dernier essai.
        var bestIndex = 0;
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] > values[bestIndex])
                bestIndex = i;
        }
        drawList.AddCircleFilled(Point(bestIndex), 3f * S, Col(SunColor));
        drawList.AddCircleFilled(Point(values.Count - 1), 2.5f * S, Col(White));
    }

    /// <summary>Ouvre un combat dans l'historique, filtres retirés, et le fait défiler jusqu'à lui.</summary>
    private void OpenRecord(string id)
    {
        filterCategory = string.Empty;
        filterPlace = string.Empty;
        filterCharacter = string.Empty;
        filterJob = string.Empty;
        filterOutcome = string.Empty;
        OnFiltersChanged();
        RefreshHistoryList();

        var index = historyList.FindIndex(r => r.Contains(id));
        if (index < 0)
            return;

        historyShown = Math.Max(HistoryPageSize, index + 1);
        expandedRun = historyList[index].Id;
        expandedAttempt = id;
        scrollToRun = expandedRun;
        page = Page.History;
    }
}
