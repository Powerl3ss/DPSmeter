using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using DPSmeter.Combat;
using DPSmeter.Core;
using DPSmeter.Windows;
using Lumina.Excel.Sheets;

namespace DPSmeter;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/dps";
    private const string LongCommandName = "/dpsmeter";
    private const string ChatPrefix = "[DPSmeter] ";
    private const string OpenWorldCategory = "Monde ouvert";

    // L'affichage est recalculé quatre fois par seconde : lisible, et sans coût à chaque image.
    private static readonly TimeSpan ViewRefresh = TimeSpan.FromMilliseconds(250);

    public Configuration Configuration { get; }
    public readonly WindowSystem WindowSystem = new("DPSmeter");

    public EncounterTracker Tracker { get; } = new();
    public CombatParser Parser { get; }
    public EncounterStore Store { get; }

    private readonly HudWindow hudWindow;
    private readonly ConfigWindow configWindow;

    private readonly Dictionary<uint, string> jobAbbreviations = new();
    private DateTime nextViewRefresh = DateTime.MinValue;
    private DateTime lastCombatUtc = DateTime.MinValue;
    private EncounterView? view;

    /// <summary>Aperçu du HUD avec de faux chiffres (réglages), pour le placer sans se battre.</summary>
    public bool PreviewMode { get; set; }

    /// <summary>Identifiant du personnage connecté (0 si personne).</summary>
    public static ulong CurrentCharacterId => ClientState.IsLoggedIn && PlayerState.IsLoaded ? PlayerState.ContentId : 0;

    /// <summary>Photo du combat affiché, ou de l'aperçu.</summary>
    public EncounterView? View => PreviewMode ? EncounterView.Sample(LocalName, PlayerState.IsLoaded ? PlayerState.ClassJob.RowId : 0) : view;

    public string LocalName => PlayerState.IsLoaded && !string.IsNullOrEmpty(PlayerState.CharacterName) ? PlayerState.CharacterName : "Toi";

    public string ZoneName { get; private set; } = string.Empty;

    /// <summary>Type de contenu de la zone actuelle (Donjons, Défis, Raids...), pour trier l'historique.</summary>
    public string ZoneCategory { get; private set; } = OpenWorldCategory;

    /// <summary>Vrai en combat, et quelques secondes après (pour laisser le temps de lire le résultat).</summary>
    public bool RecentlyInCombat =>
        Condition[ConditionFlag.InCombat] || Tracker.IsActive ||
        (DateTime.UtcNow - lastCombatUtc).TotalSeconds < Math.Max(0, Configuration.HideDelaySeconds);

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Store = new EncounterStore(
            Path.Combine(PluginInterface.GetPluginConfigDirectory(), "encounters"),
            (ex, what) => Log.Error(ex, what));
        Store.LoadInBackground();
        UpdateZoneName(ClientState.TerritoryType);

        Parser = new CombatParser(this, Tracker);
        Tracker.EncounterEnded += OnEncounterEnded;

        hudWindow = new HudWindow(this) { IsOpen = Configuration.HudVisible };
        configWindow = new ConfigWindow(this);

        WindowSystem.AddWindow(hudWindow);
        WindowSystem.AddWindow(configWindow);

        var commandInfo = new CommandInfo(OnCommand)
        {
            HelpMessage = "Affiche/masque le HUD. « /dps config » : réglages. « /dps end » : termine le combat. « /dps reset » : remet le HUD à zéro.",
        };
        CommandManager.AddHandler(CommandName, commandInfo);
        CommandManager.AddHandler(LongCommandName, new CommandInfo(OnCommand) { HelpMessage = "Comme /dps.", ShowInHelp = false });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleHud;
        ClientState.TerritoryChanged += OnTerritoryChanged;
        ClientState.Logout += OnLogout;
        Framework.Update += OnFrameworkUpdate;

        if (Parser.HookError != null)
            ChatGui.PrintError(ChatPrefix + "Les dégâts ne peuvent pas être lus avec cette version du jeu : attends une mise à jour de Dalamud ou du plugin.");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        ClientState.Logout -= OnLogout;

        // Un combat en cours à l'arrêt du plugin est enregistré comme les autres.
        Tracker.EndCurrent(DateTime.UtcNow);
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleHud;
        Tracker.EncounterEnded -= OnEncounterEnded;

        WindowSystem.RemoveAllWindows();
        hudWindow.Dispose();
        configWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(LongCommandName);

        Parser.Dispose();
        Store.Dispose();
    }

    // ---------------------------------------------------------------- Commande

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "config":
            case "settings":
                ToggleConfigUi();
                break;
            case "end":
                Tracker.EndCurrent(DateTime.UtcNow);
                break;
            case "reset":
            case "clear":
                ClearAll();
                Print("HUD remis à zéro. Les combats enregistrés sont conservés.");
                break;
            case "debug":
                Print(DiagnosticLine());
                break;
            default:
                ToggleHud();
                break;
        }
    }

    // ------------------------------------------------------- Événements du jeu

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if (now < nextViewRefresh)
            return;
        nextViewRefresh = now + ViewRefresh;

        var inCombat = Condition[ConditionFlag.InCombat];
        if (inCombat || Tracker.IsActive)
            lastCombatUtc = now;

        Tracker.Update(now, inCombat, Math.Max(1, Configuration.EndDelaySeconds));
        view = Tracker.BuildView(Tracker.Displayed, now);
    }

    private void OnTerritoryChanged(uint territory)
    {
        Tracker.EndCurrent(DateTime.UtcNow);
        UpdateZoneName(territory);
    }

    private void OnLogout(int type, int code) => Tracker.EndCurrent(DateTime.UtcNow);

    /// <summary>En instance, le nom et le type viennent de la mission. Sinon, c'est le lieu en monde ouvert.</summary>
    private void UpdateZoneName(uint territory)
    {
        var row = DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory);
        ZoneName = row?.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
        ZoneCategory = OpenWorldCategory;

        if (row?.ContentFinderCondition.ValueNullable is not { RowId: not 0 } content)
            return;

        var duty = content.Name.ExtractText();
        if (duty.Length > 0)
            ZoneName = char.ToUpper(duty[0]) + duty[1..];

        var category = content.ContentType.ValueNullable?.Name.ExtractText();
        if (!string.IsNullOrEmpty(category))
            ZoneCategory = category;
    }

    private void OnEncounterEnded(Encounter encounter)
    {
        Parser.ResetTracking();

        var ended = EncounterView.From(encounter, DateTime.UtcNow);
        if (Configuration.SaveEncounters && ended.DurationSeconds >= Configuration.MinSaveSeconds)
            Store.Add(EncounterRecord.From(encounter, ZoneCategory, ZoneName, LocalName, DateTime.UtcNow));

        if (!Configuration.ChatSummary)
            return;

        var mine = ended.Local is { } local ? $"toi {Formatting.Compact(local.Dps)} DPS ({Formatting.Percent(local.Share)})  |  " : string.Empty;
        Print($"{ended.Title}  |  {Formatting.Duration(ended.DurationSeconds)}  |  {mine}groupe {Formatting.Compact(ended.TotalDps)} DPS");
    }

    // ------------------------------------------------------------ Actions

    public void ClearAll()
    {
        Tracker.Clear();
        Parser.ResetTracking();
        view = null;
    }

    public string JobAbbreviation(uint jobId)
    {
        if (jobId == 0)
            return string.Empty;
        if (jobAbbreviations.TryGetValue(jobId, out var known))
            return known;

        var text = DataManager.GetExcelSheet<ClassJob>().GetRowOrDefault(jobId)?.Abbreviation.ToString() ?? string.Empty;
        jobAbbreviations[jobId] = text;
        return text;
    }

    /// <summary>Nom affiché : le nom du joueur, ou son job si les noms des autres sont masqués.</summary>
    public string DisplayName(CombatantView row)
    {
        if (!Configuration.HideOtherNames || row.IsLocal || row.Id == JobColors.LimitBreakId)
            return row.Name;

        var job = JobAbbreviation(row.JobId);
        return job.Length > 0 ? job : "Allié";
    }

    public string DiagnosticLine() =>
        $"Écoute {(Parser.HooksActive ? "active" : "inactive")}  |  actions lues : {Parser.ActionPackets}  |  " +
        $"coups : {Parser.DamageEffects}  |  ticks de DoT : {Parser.DotTicks}  |  DoT sans auteur : {Formatting.Full(Parser.UnattributedDotDamage)}";

    // ------------------------------------------------------------ Fenêtres

    public void ToggleConfigUi() => configWindow.Toggle();

    public void ToggleHud()
    {
        hudWindow.Toggle();
        Configuration.HudVisible = hudWindow.IsOpen;
        Configuration.Save();
    }

    public static void Print(string message) => ChatGui.Print(ChatPrefix + message);
}
