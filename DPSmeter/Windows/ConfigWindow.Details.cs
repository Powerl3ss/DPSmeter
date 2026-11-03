using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using DPSmeter.Core;

namespace DPSmeter.Windows;

/// <summary>Le détail d'un combat : tableau des joueurs, courbe de DPS et actions du joueur choisi.</summary>
public sealed partial class ConfigWindow
{
    private const int MaxChartLines = 8;

    private uint? detailPlayerId;
    private int detailActionKind;

    private void DrawEncounterDetails(EncounterView view)
    {
        DrawCombatantTable(view);
        DrawTimelineChart(view);
        DrawActionTable(view);
    }

    private Vector4 RowColor(CombatantView row)
    {
        if (row.Id == JobColors.LimitBreakId)
            return SunColor;
        return Cfg.UseJobColors ? JobColors.Of(row.JobId) ?? Accent : Accent;
    }

    /// <summary>Le joueur dont on montre les actions : celui choisi, sinon toi, sinon le premier du tableau.</summary>
    private CombatantView? DetailRow(EncounterView view) =>
        view.Combatants.FirstOrDefault(c => c.Id == detailPlayerId) ?? view.Local ?? view.Combatants.FirstOrDefault();

    // ------------------------------------------------------------ Tableau des joueurs

    private void DrawCombatantTable(EncounterView view)
    {
        Card(() =>
        {
            var dps = cardInner * 0.31f;
            var damage = cardInner * 0.40f;
            var share = cardInner * 0.50f;
            var crit = cardInner * 0.57f;
            var direct = cardInner * 0.65f;
            var healing = cardInner * 0.73f;
            var taken = cardInner * 0.83f;
            var deaths = cardInner * 0.93f;

            void Cell(float x, string text, Vector4 color, float rowStart)
            {
                ImGui.SameLine(0, 0);
                ImGui.SetCursorPosX(rowStart + x);
                ImGui.TextColored(color, text);
            }

            var headerStart = ImGui.GetCursorPosX();
            ImGui.TextColored(TextDim, "JOUEUR");
            Cell(dps, "DPS", TextDim, headerStart);
            Cell(damage, "DÉGÂTS", TextDim, headerStart);
            Cell(share, "PART", TextDim, headerStart);
            Cell(crit, "CRIT.", TextDim, headerStart);
            Cell(direct, "DIRECT", TextDim, headerStart);
            Cell(healing, "SOINS", TextDim, headerStart);
            Cell(taken, "SUBIS", TextDim, headerStart);
            Cell(deaths, "MORTS", TextDim, headerStart);
            Divider();

            var selected = DetailRow(view);
            var top = (float)Math.Max(1, view.TopDamage);
            for (var i = 0; i < view.Combatants.Count; i++)
            {
                var row = view.Combatants[i];
                var shown = plugin.DisplayName(row);
                var job = plugin.JobAbbreviation(row.JobId);
                var rowStart = ImGui.GetCursorPosX();
                var rowTop = ImGui.GetCursorScreenPos();
                var drawList = ImGui.GetWindowDrawList();

                // Le nom est coupé plutôt que de déborder sur la colonne DPS.
                var label = job.Length > 0 ? $"{i + 1}. {shown} ({job})" : $"{i + 1}. {shown}";
                ImGui.PushClipRect(rowTop, rowTop + new Vector2(dps - 6f * S, ImGui.GetTextLineHeight()), true);
                ImGui.TextColored(row.IsLocal ? Accent : TextMain, label);
                ImGui.PopClipRect();

                Cell(dps, Formatting.Compact(row.Dps), TextMain, rowStart);
                Cell(damage, Formatting.Compact(row.Damage), TextMain, rowStart);
                Cell(share, Formatting.Percent(row.Share), TextMain, rowStart);
                Cell(crit, row.Hits > 0 ? Formatting.Percent(row.CritRate) : "-", TextDim, rowStart);
                Cell(direct, row.Hits > 0 ? Formatting.Percent(row.DirectHitRate) : "-", TextDim, rowStart);
                Cell(healing, row.Healing > 0 ? Formatting.Compact(row.Healing) : "-", TextDim, rowStart);
                Cell(taken, row.DamageTaken > 0 ? Formatting.Compact(row.DamageTaken) : "-", TextDim, rowStart);
                Cell(deaths, row.Deaths > 0 ? row.Deaths.ToString() : "-", row.Deaths > 0 ? Danger : TextDim, rowStart);

                // Fine barre sous la ligne, à la couleur du joueur.
                var barTop = ImGui.GetCursorScreenPos();
                var barH = 3f * S;
                ImGui.Dummy(new Vector2(cardInner, barH));
                drawList.AddRectFilled(barTop, barTop + new Vector2(cardInner * Math.Clamp(row.Damage / top, 0f, 1f), barH), Col(RowColor(row) with { W = 0.8f }), barH / 2f);

                var rowMin = rowTop - new Vector2(4f * S, 2f * S);
                var rowMax = new Vector2(rowTop.X + cardInner + 4f * S, barTop.Y + barH + 2f * S);
                var hovered = ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(rowMin, rowMax);
                if (row.Id == selected?.Id)
                    drawList.AddRect(rowMin, rowMax, Col(AccentBorder), 5f * S);

                if (hovered)
                {
                    drawList.AddRectFilled(rowMin, rowMax, Col(HoverOverlay), 5f * S);
                    Tip.Show(Formatting.Tooltip(row, shown, job, view.DurationSeconds, Cfg.RecentWindowSeconds) + "\n\nClic : voir toutes ses actions");
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                        detailPlayerId = row.Id;
                }
            }
        });
    }

    // ------------------------------------------------------------ Courbe de DPS

    private void DrawTimelineChart(EncounterView view)
    {
        Card(() =>
        {
            CardLabel("Évolution du DPS");

            if (!view.HasTimeline)
            {
                TextDimmed("Courbe indisponible : ce combat a été enregistré avant qu'elle n'existe.");
                return;
            }

            var rows = view.Combatants
                .Where(c => c.Id != JobColors.LimitBreakId && c.Damage > 0 && c.Timeline is { Count: > 0 })
                .Take(MaxChartLines)
                .ToList();
            var points = rows.Count > 0 ? rows.Max(r => r.Timeline!.Count) : 0;
            if (points < 2)
            {
                TextDimmed("Combat trop court pour tracer une courbe.");
                return;
            }

            DrawChartLegend(rows);

            var height = 170f * S;
            var origin = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(cardInner, height));

            var plotMin = origin + new Vector2(48f * S, 4f * S);
            var plotMax = origin + new Vector2(cardInner - 6f * S, height - 20f * S);
            var plotSize = plotMax - plotMin;
            var stepX = plotSize.X / (points - 1);

            var maxDps = 1.0;
            foreach (var row in rows)
            {
                for (var i = 0; i < row.Timeline!.Count; i++)
                    maxDps = Math.Max(maxDps, view.TimelineDps(row, i));
            }

            var drawList = ImGui.GetWindowDrawList();

            // Lignes de repère et échelle du DPS.
            for (var i = 0; i <= 3; i++)
            {
                var y = plotMax.Y - plotSize.Y * i / 3f;
                drawList.AddLine(new Vector2(plotMin.X, y), new Vector2(plotMax.X, y), Col(Line));
                var label = Formatting.Compact(maxDps * i / 3.0);
                drawList.AddText(new Vector2(plotMin.X - ImGui.CalcTextSize(label).X - 6f * S, y - ImGui.GetTextLineHeight() / 2f), Col(TextDim), label);
            }

            // Échelle du temps.
            for (var i = 0; i <= 4; i++)
            {
                var x = plotMin.X + plotSize.X * i / 4f;
                var label = Formatting.Duration((points - 1) * (double)Encounter.TimelineStepSeconds * i / 4.0);
                var labelWidth = ImGui.CalcTextSize(label).X;
                drawList.AddText(new Vector2(Math.Clamp(x - labelWidth / 2f, plotMin.X, plotMax.X - labelWidth), plotMax.Y + 4f * S), Col(TextDim), label);
            }

            Vector2 Point(CombatantView row, int index) => new(
                plotMin.X + index * stepX,
                plotMax.Y - (float)(view.TimelineDps(row, index) / maxDps) * plotSize.Y);

            // Les autres d'abord : ta courbe reste par-dessus.
            foreach (var row in rows.OrderBy(r => r.IsLocal))
            {
                for (var i = 0; i < row.Timeline!.Count; i++)
                    drawList.PathLineTo(Point(row, i));
                drawList.PathStroke(Col(RowColor(row) with { W = row.IsLocal ? 1f : 0.75f }), ImDrawFlags.None, (row.IsLocal ? 2.2f : 1.3f) * S);
            }

            // Morts : un point rouge au moment où le joueur est tombé.
            foreach (var row in rows)
            {
                foreach (var seconds in row.DeathSeconds ?? Array.Empty<double>())
                {
                    var x = plotMin.X + (float)(seconds / Encounter.TimelineStepSeconds) * stepX;
                    if (x <= plotMax.X)
                        drawList.AddCircleFilled(new Vector2(x, plotMax.Y), 3.5f * S, Col(Danger));
                }
            }

            if (!ImGui.IsWindowHovered() || !ImGui.IsMouseHoveringRect(plotMin, plotMax))
                return;

            var hoveredIndex = Math.Clamp((int)MathF.Round((ImGui.GetMousePos().X - plotMin.X) / stepX), 0, points - 1);
            var hoverX = plotMin.X + hoveredIndex * stepX;
            drawList.AddLine(new Vector2(hoverX, plotMin.Y), new Vector2(hoverX, plotMax.Y), Col(TextDim));
            Tip.Show(ChartTooltip(view, rows, hoveredIndex));
        });
    }

    private void DrawChartLegend(List<CombatantView> rows)
    {
        var used = 0f;
        var square = ImGui.GetTextLineHeight() * 0.6f;
        for (var i = 0; i < rows.Count; i++)
        {
            var name = plugin.DisplayName(rows[i]);
            var width = square + 5f * S + ImGui.CalcTextSize(name).X;
            if (i > 0 && used + width + 14f * S <= cardInner)
            {
                ImGui.SameLine(0, 14f * S);
                used += 14f * S;
            }
            else
            {
                used = 0f;
            }

            var pos = ImGui.GetCursorScreenPos();
            var top = pos.Y + (ImGui.GetTextLineHeight() - square) / 2f;
            ImGui.GetWindowDrawList().AddRectFilled(new Vector2(pos.X, top), new Vector2(pos.X + square, top + square), Col(RowColor(rows[i])), 2f * S);
            ImGui.SetCursorScreenPos(pos + new Vector2(square + 5f * S, 0));
            ImGui.TextColored(rows[i].IsLocal ? TextMain : TextDim, name);
            used += width;
        }
    }

    private string ChartTooltip(EncounterView view, List<CombatantView> rows, int index)
    {
        var from = index * Encounter.TimelineStepSeconds;
        var lines = new List<string> { $"{Formatting.Duration(from)} - {Formatting.Duration(from + Encounter.TimelineStepSeconds)}" };

        foreach (var row in rows.OrderByDescending(r => view.TimelineDps(r, index)))
            lines.Add($"{plugin.DisplayName(row)} : {Formatting.Compact(view.TimelineDps(row, index))} DPS");

        foreach (var row in rows)
        {
            foreach (var seconds in row.DeathSeconds ?? Array.Empty<double>())
            {
                if (seconds >= from && seconds < from + Encounter.TimelineStepSeconds)
                    lines.Add($"Mort : {plugin.DisplayName(row)} à {Formatting.Duration(seconds)}");
            }
        }

        return string.Join("\n", lines);
    }

    // ------------------------------------------------------------ Actions du joueur

    private void DrawActionTable(EncounterView view)
    {
        if (DetailRow(view) is not { } row)
            return;

        Card(() =>
        {
            CardLabel($"Actions | {plugin.DisplayName(row)}");

            var heals = row.HealActions ?? Array.Empty<ActionView>();
            var hasHeals = heals.Count > 0;
            if (hasHeals)
            {
                detailActionKind = Segmented("##actionKind", new[]
                {
                    ("Dégâts", FontAwesomeIcon.Fire),
                    ("Soins", FontAwesomeIcon.Heart),
                }, detailActionKind, cardInner);
            }

            var showHeals = hasHeals && detailActionKind == 1;
            var actions = showHeals ? heals : row.Actions ?? Array.Empty<ActionView>();
            if (actions.Count == 0)
            {
                TextDimmed("Aucune action enregistrée.");
                return;
            }

            var amount = cardInner * 0.50f;
            var share = cardInner * 0.64f;
            var hits = cardInner * 0.76f;
            var maxHit = cardInner * 0.87f;

            void Cell(float x, string text, Vector4 color, float rowStart)
            {
                ImGui.SameLine(0, 0);
                ImGui.SetCursorPosX(rowStart + x);
                ImGui.TextColored(color, text);
            }

            var headerStart = ImGui.GetCursorPosX();
            // Pas de nom pour cette colonne : le titre de la carte dit déjà qu'il s'agit des actions.
            ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeight()));
            Cell(amount, showHeals ? "SOINS" : "DÉGÂTS", TextDim, headerStart);
            Cell(share, "PART", TextDim, headerStart);
            Cell(hits, "COUPS", TextDim, headerStart);
            Cell(maxHit, "MAX", TextDim, headerStart);
            Divider();

            var top = (float)Math.Max(1, actions[0].Damage);
            foreach (var action in actions)
            {
                var rowStart = ImGui.GetCursorPosX();
                var rowTop = ImGui.GetCursorScreenPos();

                ImGui.PushClipRect(rowTop, rowTop + new Vector2(amount - 6f * S, ImGui.GetTextLineHeight()), true);
                ImGui.TextColored(TextMain, action.Name);
                ImGui.PopClipRect();

                Cell(amount, Formatting.Compact(action.Damage), TextMain, rowStart);
                Cell(share, Formatting.Percent(action.Share), TextMain, rowStart);
                Cell(hits, action.Hits.ToString(), TextDim, rowStart);
                Cell(maxHit, action.MaxHit > 0 ? Formatting.Compact(action.MaxHit) : "-", TextDim, rowStart);

                var barTop = ImGui.GetCursorScreenPos();
                var barH = 2f * S;
                ImGui.Dummy(new Vector2(cardInner, barH));
                ImGui.GetWindowDrawList().AddRectFilled(barTop, barTop + new Vector2(cardInner * Math.Clamp(action.Damage / top, 0f, 1f), barH), Col(RowColor(row) with { W = 0.65f }), barH / 2f);
            }
        });
    }
}
