using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using DPSmeter.Core;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Network;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using BattleNpcSubKind = Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaStatus = Lumina.Excel.Sheets.Status;

namespace DPSmeter.Combat;

/// <summary>
/// Écoute les paquets de combat reçus par le jeu et les transforme en coups attribués à un joueur.
/// Deux sources : les effets d'actions (coups directs) et les ActorControl (ticks de DoT).
/// Le plugin ne fait qu'écouter : l'appel d'origine est toujours exécuté, et rien n'est envoyé au serveur.
/// </summary>
public sealed unsafe class CombatParser : IDisposable
{
    private const uint InvalidId = 0xE0000000;

    // Catégories d'ActorControl pour les ticks (valeurs du client global depuis le patch 7.1).
    private const uint CategoryDot = 0x605;

    // Types d'effets d'une action.
    private const byte EffectDamage = 3;
    private const byte EffectBlockedDamage = 5;
    private const byte EffectParriedDamage = 6;

    // Param0 d'un effet de dégâts.
    private const byte FlagCrit = 0x20;
    private const byte FlagDirectHit = 0x40;

    // Param4 d'un effet de dégâts.
    private const byte FlagFromTarget = 0x20;  // renvoyé par la cible (riposte d'un tank...)
    private const byte FlagLargeValue = 0x40;  // Param3 contient les bits de poids fort du montant
    private const byte FlagAtSource = 0x80;    // subi par le lanceur

    private const uint ActionTypeItem = 2;

    private const int MaxTrackedSequences = 4096;

    private readonly Plugin plugin;
    private readonly EncounterTracker tracker;

    private readonly Hook<ActionEffectHandler.Delegates.Receive>? actionEffectHook;
    private readonly Hook<PacketDispatcher.Delegates.HandleActorControlPacket>? actorControlHook;

    private readonly Dictionary<uint, (string Name, bool IsLimitBreak)> actionCache = new();
    private readonly Dictionary<uint, string> statusCache = new();

    // Un même effet peut être rejoué : on ne le compte qu'une fois.
    private readonly HashSet<(uint Caster, uint Sequence, uint Target, int Index)> seenEffects = new();
    private readonly Queue<(uint Caster, uint Sequence, uint Target, int Index)> seenOrder = new();

    // Dernier auteur connu de chaque DoT : le dernier tick arrive parfois juste après la fin de l'effet.
    private readonly Dictionary<(uint Target, uint Status), (DotShare[] Shares, DateTime SeenUtc)> lastDotSources = new();
    private readonly List<DotShare> dotCandidates = new();

    /// <summary>Vrai si les deux écoutes du jeu sont en place.</summary>
    public bool HooksActive => actionEffectHook != null && actorControlHook != null;
    public string? HookError { get; }

    // Compteurs affichés dans l'onglet « À propos » pour vérifier que tout est bien reçu.
    public long ActionPackets { get; private set; }
    public long DamageEffects { get; private set; }
    public long DotTicks { get; private set; }
    public long UnattributedDotDamage { get; private set; }
    public DateTime? LastPacketUtc { get; private set; }

    public CombatParser(Plugin plugin, EncounterTracker tracker)
    {
        this.plugin = plugin;
        this.tracker = tracker;

        try
        {
            actionEffectHook = Plugin.GameInterop.HookFromAddress(
                (nint)ActionEffectHandler.MemberFunctionPointers.Receive,
                new ActionEffectHandler.Delegates.Receive(ActionEffectDetour));
            actorControlHook = Plugin.GameInterop.HookFromAddress(
                (nint)PacketDispatcher.MemberFunctionPointers.HandleActorControlPacket,
                new PacketDispatcher.Delegates.HandleActorControlPacket(ActorControlDetour));

            actionEffectHook.Enable();
            actorControlHook.Enable();
        }
        catch (Exception ex)
        {
            // Après une mise à jour du jeu, une signature peut ne plus être trouvée : le plugin reste chargé.
            Plugin.Log.Error(ex, "Impossible d'écouter les paquets de combat");
            HookError = ex.Message;
            actionEffectHook?.Dispose();
            actorControlHook?.Dispose();
            actionEffectHook = null;
            actorControlHook = null;
        }
    }

    public void Dispose()
    {
        actionEffectHook?.Dispose();
        actorControlHook?.Dispose();
    }

    /// <summary>Oublie la mémoire de court terme (fin de combat, remise à zéro).</summary>
    public void ResetTracking()
    {
        seenEffects.Clear();
        seenOrder.Clear();
        lastDotSources.Clear();
    }

    // ---------------------------------------------------------------- Coups directs

    private void ActionEffectDetour(
        uint casterEntityId, Character* casterPtr, Vector3* targetPos,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        actionEffectHook!.Original(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);

        try
        {
            if (header != null && effects != null && targetEntityIds != null)
                ReadActionEffect(casterEntityId, header, effects, targetEntityIds);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Lecture d'un effet d'action");
        }
    }

    private void ReadActionEffect(uint casterId, ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetIds)
    {
        ActionPackets++;
        LastPacketUtc = DateTime.UtcNow;

        var (actionName, isLimitBreak) = ReadAction(header);

        for (var i = 0; i < header->NumTargets; i++)
        {
            var targetId = (uint)targetIds[i].ObjectId;
            if (IsDuplicate(casterId, header->GlobalSequence, targetId, i))
                continue;

            for (var j = 0; j < 8; j++)
            {
                ref var effect = ref effects[i].Effects[j];
                if (effect.Type is not (EffectDamage or EffectBlockedDamage or EffectParriedDamage))
                    continue;

                long amount = effect.Value;
                if ((effect.Param4 & FlagLargeValue) != 0)
                    amount += (long)effect.Param3 << 16;
                if (amount <= 0)
                    continue;

                DamageEffects++;

                // Riposte : c'est la cible qui inflige des dégâts au lanceur.
                var fromTarget = (effect.Param4 & FlagFromTarget) != 0;
                var dealerId = fromTarget ? targetId : casterId;
                var recipientId = (effect.Param4 & FlagAtSource) != 0 ? casterId : targetId;
                if (dealerId == recipientId)
                    continue;

                var recipient = Plugin.ObjectTable.SearchByEntityId(recipientId);
                if (!IsEnemy(recipient) || !TryResolveSource(dealerId, out var source))
                    continue;

                if (isLimitBreak && plugin.Configuration.SeparateLimitBreak)
                    source = new Source(JobColors.LimitBreakId, "Transcendance", JobColors.LimitBreakId, false);

                var hit = new Hit(source.Id, source.Name, source.JobId, source.IsLocal,
                                  recipient?.Name.TextValue ?? string.Empty,
                                  fromTarget ? "Riposte" : actionName,
                                  amount,
                                  (effect.Param0 & FlagCrit) != 0,
                                  (effect.Param0 & FlagDirectHit) != 0);
                tracker.Record(hit, DateTime.UtcNow, plugin.ZoneName);
            }
        }
    }

    private (string Name, bool IsLimitBreak) ReadAction(ActionEffectHandler.Header* header)
    {
        if (header->ActionType == ActionTypeItem)
            return ("Objet", false);

        var id = header->SpellId != 0 ? header->SpellId : header->ActionId;
        if (actionCache.TryGetValue(id, out var known))
            return known;

        var row = Plugin.DataManager.GetExcelSheet<LuminaAction>().GetRowOrDefault(id);
        var name = row?.Name.ToString();
        var info = (string.IsNullOrEmpty(name) ? $"Action {id}" : name, row?.ActionCategory.RowId == 9);
        actionCache[id] = info;
        return info;
    }

    private bool IsDuplicate(uint caster, uint sequence, uint target, int index)
    {
        if (sequence == 0)
            return false;

        var key = (caster, sequence, target, index);
        if (!seenEffects.Add(key))
            return true;

        seenOrder.Enqueue(key);
        if (seenOrder.Count > MaxTrackedSequences)
            seenEffects.Remove(seenOrder.Dequeue());
        return false;
    }

    // ---------------------------------------------------------------- Ticks de DoT

    private void ActorControlDetour(
        uint entityId, uint category, uint arg1, uint arg2, uint arg3, uint arg4,
        uint arg5, uint arg6, uint arg7, uint arg8, GameObjectId targetId, bool isRecorded)
    {
        actorControlHook!.Original(entityId, category, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, targetId, isRecorded);

        if (category != CategoryDot)
            return;

        try
        {
            // arg1 : effet (0 si le tick n'en désigne pas), arg2 : montant, arg3 : source indiquée par le paquet.
            ReadDotTick(entityId, arg1, arg2, arg3);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Lecture d'un tick de DoT");
        }
    }

    private void ReadDotTick(uint targetId, uint statusId, uint amount, uint packetSource)
    {
        DotTicks++;
        LastPacketUtc = DateTime.UtcNow;

        if (amount == 0 || !tracker.IsActive)
            return;

        var target = Plugin.ObjectTable.SearchByEntityId(targetId);
        if (!IsEnemy(target))
            return;

        FindDotSources(target as IBattleChara, targetId, statusId, packetSource);
        if (dotCandidates.Count == 0)
        {
            UnattributedDotDamage += amount;
            return;
        }

        var estimated = dotCandidates.Count > 1;
        foreach (var share in DotTable.Split(amount, dotCandidates))
        {
            if (share.Amount == 0 || !TryResolveSource(share.SourceId, out var source))
                continue;

            var hit = new Hit(source.Id, source.Name, source.JobId, source.IsLocal,
                              target?.Name.TextValue ?? string.Empty,
                              StatusName(share.StatusId != 0 ? share.StatusId : statusId),
                              share.Amount, IsDot: true, IsEstimated: estimated);
            tracker.Record(hit, DateTime.UtcNow, plugin.ZoneName);
        }
    }

    /// <summary>Remplit <see cref="dotCandidates"/> avec les joueurs à qui ce tick peut revenir.</summary>
    private void FindDotSources(IBattleChara? target, uint targetId, uint statusId, uint packetSource)
    {
        dotCandidates.Clear();

        if (statusId != 0)
        {
            // Effet au sol : le paquet donne directement son auteur.
            if (DotTable.IsGround(statusId) && IsValidId(packetSource))
            {
                dotCandidates.Add(new DotShare(packetSource, statusId, 1f));
                return;
            }

            // Même effet posé par plusieurs joueurs : à parts égales.
            AddStatusesOn(target, s => s == statusId, weighted: false);
            if (dotCandidates.Count > 0)
            {
                Remember(targetId, statusId);
                return;
            }

            // Effet déjà terminé au moment du tick.
            if (lastDotSources.TryGetValue((targetId, statusId), out var last) &&
                (DateTime.UtcNow - last.SeenUtc).TotalSeconds < 6)
            {
                dotCandidates.AddRange(last.Shares);
                return;
            }

            if (IsValidId(packetSource))
            {
                dotCandidates.Add(new DotShare(packetSource, statusId, 1f));
                return;
            }

            // Effet au sol porté par son auteur (Doton...) : valable seulement si un seul joueur l'a actif.
            if (DotTable.IsGround(statusId))
                AddUniqueGroundOwner(statusId);
            return;
        }

        // Aucun effet désigné : d'abord un effet au sol sur la cible, puis la source du paquet,
        // et sinon un tick groupé, partagé entre tous les DoT présents selon leur puissance.
        AddStatusesOn(target, DotTable.IsGround, weighted: true);
        if (dotCandidates.Count > 0)
            return;

        if (IsValidId(packetSource))
        {
            dotCandidates.Add(new DotShare(packetSource, 0, 1f));
            return;
        }

        AddStatusesOn(target, DotTable.IsKnownDot, weighted: true);
    }

    private void AddStatusesOn(IBattleChara? target, Func<uint, bool> matches, bool weighted)
    {
        if (target == null)
            return;

        foreach (var status in target.StatusList)
        {
            if (status.StatusId != 0 && matches(status.StatusId) && IsValidId(status.SourceId))
                dotCandidates.Add(new DotShare(status.SourceId, status.StatusId, weighted ? DotTable.WeightOf(status.StatusId) : 1f));
        }
    }

    private void AddUniqueGroundOwner(uint statusId)
    {
        uint owner = 0;
        foreach (var player in Plugin.ObjectTable.PlayerObjects)
        {
            foreach (var status in player.StatusList)
            {
                if (status.StatusId != statusId || !IsValidId(status.SourceId))
                    continue;
                if (owner != 0 && owner != status.SourceId)
                    return;
                owner = status.SourceId;
            }
        }

        if (owner != 0)
            dotCandidates.Add(new DotShare(owner, statusId, 1f));
    }

    private void Remember(uint targetId, uint statusId)
    {
        // Mémoire bornée : on repart de zéro plutôt que de la laisser grossir dans un très long combat.
        if (lastDotSources.Count > 512)
            lastDotSources.Clear();
        lastDotSources[(targetId, statusId)] = (dotCandidates.ToArray(), DateTime.UtcNow);
    }

    private string StatusName(uint statusId)
    {
        if (statusId == 0)
            return "DoT";

        if (statusCache.TryGetValue(statusId, out var known))
            return known;

        var name = Plugin.DataManager.GetExcelSheet<LuminaStatus>().GetRowOrDefault(statusId)?.Name.ToString();
        var label = string.IsNullOrEmpty(name) ? "DoT" : $"{name} (DoT)";
        statusCache[statusId] = label;
        return label;
    }

    // ---------------------------------------------------------------- Qui est qui

    private readonly record struct Source(uint Id, string Name, uint JobId, bool IsLocal);

    private static bool IsValidId(uint id) => id is not (0 or InvalidId);

    /// <summary>Retrouve la ligne du tableau d'un acteur, s'il doit y figurer. Un familier revient à son maître.</summary>
    private bool TryResolveSource(uint entityId, out Source source)
    {
        source = default;
        if (!IsValidId(entityId))
            return false;

        var actor = Plugin.ObjectTable.SearchByEntityId(entityId);
        if (actor == null)
            return false;

        var config = plugin.Configuration;

        if (actor is IPlayerCharacter player)
        {
            if (!IsInScope(player.EntityId, config.Scope))
                return false;
            source = FromPlayer(player);
            return true;
        }

        if (actor is not IBattleNpc npc)
            return false;

        // PNJ allié d'un groupe avec soutien de mission.
        if (npc.BattleNpcKind == BattleNpcSubKind.NpcPartyMember)
        {
            if (config.Scope == MeterScope.Self)
                return false;
            source = new Source(entityId, npc.Name.TextValue, npc.ClassJob.RowId, false);
            return true;
        }

        if (!IsValidId(npc.OwnerId) ||
            Plugin.ObjectTable.SearchByEntityId(npc.OwnerId) is not IPlayerCharacter owner ||
            !IsInScope(owner.EntityId, config.Scope))
            return false;

        // Le chocobo garde sa propre ligne : ce n'est pas un familier de job.
        if (npc.BattleNpcKind == BattleNpcSubKind.Buddy || !config.MergePets)
        {
            source = new Source(entityId, npc.Name.TextValue, 0, false);
            return true;
        }

        source = FromPlayer(owner);
        return true;
    }

    private static Source FromPlayer(IPlayerCharacter player) =>
        new(player.EntityId, player.Name.TextValue, player.ClassJob.RowId, player.EntityId == Plugin.ObjectTable.LocalPlayer?.EntityId);

    private static bool IsInScope(uint entityId, MeterScope scope)
    {
        if (entityId == Plugin.ObjectTable.LocalPlayer?.EntityId)
            return true;

        switch (scope)
        {
            case MeterScope.Self:
                return false;

            case MeterScope.AllPlayers:
                return true;
        }

        var group = GroupManager.Instance();
        if (group != null)
        {
            if (group->MainGroup.IsEntityIdInParty(entityId))
                return true;
            if (scope == MeterScope.Alliance && group->MainGroup.IsEntityIdInAlliance(entityId))
                return true;
        }

        // Groupe interserveurs vu depuis le monde ouvert : la liste du jeu est ailleurs.
        return InfoProxyCrossRealm.IsCrossRealmParty() && InfoProxyCrossRealm.GetMemberByEntityId(entityId) != null;
    }

    /// <summary>Seuls les coups sur des ennemis comptent (pas un soin qui blesse, pas l'allié d'une mécanique).</summary>
    private static bool IsEnemy(IGameObject? target)
    {
        // Cible déjà disparue de la liste des objets : c'est un ennemi qui vient de mourir.
        if (target == null)
            return true;

        return target switch
        {
            IPlayerCharacter => Plugin.ClientState.IsPvP,
            IBattleNpc { BattleNpcKind: BattleNpcSubKind.Pet or BattleNpcSubKind.Buddy or BattleNpcSubKind.NpcPartyMember } => Plugin.ClientState.IsPvP,
            _ => true,
        };
    }
}
