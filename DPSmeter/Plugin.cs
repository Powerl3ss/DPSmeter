using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Game.DutyState;
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
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/dps";
    private const string LongCommandName = "/dpsmeter";
    private const string ChatPrefix = "[DPSmeter] ";
    private const string OpenWorldCategory = "Monde ouvert";

    // Délai pendant lequel une annonce du jeu (victoire, équipe vaincue) peut encore corriger un combat terminé.
    private const double OutcomeGraceSeconds = 60;

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
    private (Encounter Encounter, EncounterRecord Record)? lastSaved;
    private readonly HashSet<uint> deadNow = new();
    private int wipesThisRun;
    private string currentRunId = string.Empty;
    private int endingWipeNumber;

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

    /// <summary>En instance, avec un seul combat du premier coup jusqu'à la sortie (ou jusqu'au wipe).</summary>
    public bool IsDutyWideMode => Configuration.KeepEncounterInDuty && Condition[ConditionFlag.BoundByDuty];

    /// <summary>L'instance est terminée : on n'ouvre plus de combat jusqu'à la sortie, l'affichage garde le résultat.</summary>
    public bool IsDutyOver { get; private set; }

    /// <summary>Vrai en combat, et quelques secondes après (pour laisser le temps de lire le résultat).</summary>
    public bool RecentlyInCombat =>
        Condition[ConditionFlag.InCombat] || Tracker.IsActive || IsDutyWideMode ||
        (DateTime.UtcNow - lastCombatUtc).TotalSeconds < Math.Max(0, Configuration.HideDelaySeconds);

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        // Infobulles : même style dans les trois plugins, avec la couleur d'accent choisie.
        DPSmeter.Windows.Tip.AccentProvider = () => Configuration.AccentColor;
        Store = new EncounterStore(
            Path.Combine(PluginInterface.GetPluginConfigDirectory(), "encounters"),
            (ex, what) => Log.Error(ex, what));
        Store.LoadInBackground();
        PruneHistory();
        UpdateZoneName(ClientState.TerritoryType);
        StartRun();

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
        DutyState.DutyCompleted += OnDutyCompleted;
        DutyState.DutyWiped += OnDutyWiped;
        DutyState.DutyRecommenced += OnDutyRecommenced;
        Framework.Update += OnFrameworkUpdate;

        if (Parser.HookError != null)
            ChatGui.PrintError(ChatPrefix + "Les dégâts ne peuvent pas être lus avec cette version du jeu : attends une mise à jour de Dalamud ou du plugin.");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        ClientState.Logout -= OnLogout;
        DutyState.DutyCompleted -= OnDutyCompleted;
        DutyState.DutyWiped -= OnDutyWiped;
        DutyState.DutyRecommenced -= OnDutyRecommenced;

        // Un combat en cours à l'arrêt du plugin est enregistré comme les autres.
        Tracker.EndCurrent(DateTime.UtcNow);
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleHud;
        Tracker.EncounterEnded -= OnEncounterEnded;

        WindowSystem.RemoveAllWindows();
        hudWindow.Dispose();
        configWindow.Dispose();
        DPSmeter.Windows.Tip.Dispose();

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
        // Écran de chargement : rien à recalculer, le HUD est caché et aucun coup n'arrive.
        if (Condition[ConditionFlag.BetweenAreas] || Condition[ConditionFlag.BetweenAreas51])
            return;

        var now = DateTime.UtcNow;
        if (now < nextViewRefresh)
            return;
        nextViewRefresh = now + ViewRefresh;

        var inCombat = Condition[ConditionFlag.InCombat];
        if (inCombat || Tracker.IsActive)
            lastCombatUtc = now;

        // Seulement en combat : après un wipe, le boss récupère tous ses PV, ce qui fausserait les PV restants.
        if (inCombat)
            Parser.PollTargets();

        Tracker.RecentWindowSeconds = Configuration.RecentWindowSeconds;
        Tracker.Update(now, inCombat, Math.Max(1, Configuration.EndDelaySeconds));
        view = Tracker.BuildView(Tracker.Displayed, now);
        RefreshDeadPlayers();
    }

    private void OnTerritoryChanged(uint territory)
    {
        Tracker.EndCurrent(DateTime.UtcNow);
        UpdateZoneName(territory);

        IsDutyOver = false;
        wipesThisRun = 0;
        StartRun();

        // Nouvelle instance : le compteur repart de zéro au lieu de montrer le combat précédent.
        if (Configuration.KeepEncounterInDuty && ZoneCategory != OpenWorldCategory)
            Tracker.HideLastEnded();
    }

    /// <summary>Chaque entrée dans une instance ouvre une nouvelle entrée de l'historique. Hors instance, chaque combat est seul.</summary>
    private void StartRun() =>
        currentRunId = ZoneCategory != OpenWorldCategory ? Guid.NewGuid().ToString("N") : string.Empty;

    private void OnLogout(int type, int code) => Tracker.EndCurrent(DateTime.UtcNow);

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        IsDutyOver = true;
        var now = DateTime.UtcNow;

        // Plus rien à mesurer : le combat se fige sur son résultat, qui reste affiché jusqu'à la sortie.
        if (Tracker.IsCurrentDutyWide)
        {
            Tracker.MarkOutcome(Outcome.Victory);
            Tracker.EndCurrent(now);
            return;
        }

        ApplyDutyOutcome(Outcome.Victory);
    }

    private void OnDutyWiped(IDutyStateEventArgs args) => HandleWipe();

    /// <summary>La partie repart : si le wipe n'a pas été annoncé, le combat en cours en était un.</summary>
    private void OnDutyRecommenced(IDutyStateEventArgs args)
    {
        IsDutyOver = false;
        if (Tracker.IsCurrentDutyWide)
            HandleWipe();
    }

    /// <summary>Le groupe est vaincu : le wipe est enregistré avec ses dégâts, et le compteur repart de zéro.</summary>
    private void HandleWipe()
    {
        if (Tracker.IsActive)
        {
            wipesThisRun++;
            Tracker.MarkOutcome(Outcome.Failure);
            endingWipeNumber = wipesThisRun;
            Tracker.EndCurrent(DateTime.UtcNow);
            endingWipeNumber = 0;
        }
        else if (lastSaved is { Record.WipeNumber: 0 })
        {
            // Combat terminé juste avant l'annonce. Un combat déjà compté comme wipe ne l'est pas deux fois.
            wipesThisRun++;
            ApplyDutyOutcome(Outcome.Failure);
        }

        Tracker.HideLastEnded();
        view = null;
    }

    /// <summary>L'instance annonce son issue : sur le combat en cours, ou sur celui qui vient de se terminer.</summary>
    private void ApplyDutyOutcome(Outcome outcome)
    {
        if (Tracker.MarkOutcome(outcome))
            return;

        var marked = Tracker.MarkLastEnded(outcome, DateTime.UtcNow, OutcomeGraceSeconds);
        if (marked == null || lastSaved is not { } saved || !ReferenceEquals(saved.Encounter, marked))
            return;

        var updated = saved.Record with
        {
            View = saved.Record.View with { Outcome = marked.Outcome, RemainingHp = marked.RemainingHp },
            WipeNumber = outcome == Outcome.Failure ? wipesThisRun : saved.Record.WipeNumber,
        };
        lastSaved = (marked, updated);
        Store.Add(updated);
    }

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
        // Un wipe est toujours enregistré, même très court : chacun doit se retrouver dans l'historique.
        if (Configuration.SaveEncounters && (endingWipeNumber > 0 || ended.DurationSeconds >= Configuration.MinSaveSeconds))
        {
            var record = EncounterRecord.From(encounter, ZoneCategory, ZoneName, LocalName, DateTime.UtcNow, endingWipeNumber, currentRunId);
            if (Configuration.ChatRecords && Store.IsLoaded && PersonalBests.BeatsPrevious(record, Store.All()))
                Print($"Nouveau record sur {record.View.Title} : {Formatting.Full(record.View.Local!.Dps)} DPS ({JobAbbreviation(record.View.Local!.JobId)}) !");

            Store.Add(record);
            PruneHistory();
            lastSaved = (encounter, record);
        }

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

    /// <summary>Ce joueur est-il mort en ce moment ? (l'aperçu du HUD s'appuie sur ses faux chiffres)</summary>
    public bool IsDeadNow(CombatantView row) => PreviewMode ? row.Deaths > 0 : deadNow.Contains(row.Id);

    /// <summary>Lit l'état réel des joueurs du tableau : mort, ou de nouveau en vie après une résurrection.</summary>
    private void RefreshDeadPlayers()
    {
        deadNow.Clear();
        if (view == null)
            return;

        foreach (var row in view.Combatants)
        {
            if (ObjectTable.SearchByEntityId(row.Id) is IBattleChara { CurrentHp: 0 } or IBattleChara { IsDead: true })
                deadNow.Add(row.Id);
        }
    }

    /// <summary>Supprime les plus anciens combats au-delà de la limite choisie (en arrière-plan).</summary>
    public void PruneHistory() => Store.PruneInBackground(Configuration.MaxSavedEncounters, Configuration.PinnedRuns);

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
        $"coups : {Parser.DamageEffects}  |  ticks de DoT : {Parser.DotTicks}  |  DoT sans auteur : {Formatting.Full(Parser.UnattributedDotDamage)}  |  " +
        $"soins : {Parser.HealEffects}  |  ticks de HoT : {Parser.HotTicks}  |  morts vues : {Parser.Deaths}";

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
