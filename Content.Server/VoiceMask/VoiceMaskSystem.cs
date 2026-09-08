// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Goobstation.Shared.IntrinsicVoiceModulator.VoiceMask; // Goobstation
using Content.Server.Speech;
using Content.Shared.Actions;
using Content.Shared.Administration.Logs;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Chat.RadioIconsEvents; // Goobstation
using Content.Shared.Clothing;
using Content.Shared.Database;
using Content.Shared.IdentityManagement;
using Content.Shared.IdentityManagement.Components;
using Content.Shared.Implants;
using Content.Shared.Inventory;
using Content.Shared.Lock;
using Content.Shared.Popups;
using Content.Shared.Roles.Jobs; // Goobstation
using Content.Shared.Speech;
using Content.Shared.VoiceMask;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Server.VoiceMask;

public sealed partial class VoiceMaskSystem : EntitySystem
{
    [Dependency] private readonly SharedUserInterfaceSystem _uiSystem = default!;
    [Dependency] private readonly SharedPopupSystem _popupSystem = default!;
    [Dependency] private readonly IConfigurationManager _cfgManager = default!;
    [Dependency] private readonly ISharedAdminLogManager _adminLogger = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly LockSystem _lock = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly IdentitySystem _identity = default!;

    // CCVar.
    private int _maxNameLength;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<VoiceMaskComponent, InventoryRelayedEvent<TransformSpeakerNameEvent>>(OnTransformSpeakerNameInventory);
        SubscribeLocalEvent<VoiceMaskComponent, ImplantRelayEvent<TransformSpeakerNameEvent>>(OnTransformSpeakerNameImplant);
        SubscribeLocalEvent<VoiceMaskComponent, ImplantRelayEvent<SeeIdentityAttemptEvent>>(OnSeeIdentityAttemptEvent);
        SubscribeLocalEvent<VoiceMaskComponent, ImplantImplantedEvent>(OnImplantImplantedEvent);
        SubscribeLocalEvent<VoiceMaskComponent, ImplantRemovedEvent>(OnImplantRemovedEventEvent);
        SubscribeLocalEvent<VoiceMaskComponent, LockToggledEvent>(OnLockToggled);
        SubscribeLocalEvent<VoiceMaskComponent, VoiceMaskChangeNameMessage>(OnChangeName);
        SubscribeLocalEvent<VoiceMaskComponent, VoiceMaskChangeVerbMessage>(OnChangeVerb);
        SubscribeLocalEvent<VoiceMaskComponent, VoiceMaskToggleMessage>(OnToggle);
        SubscribeLocalEvent<VoiceMaskComponent, VoiceMaskAccentToggleMessage>(OnAccentToggle);
        SubscribeLocalEvent<VoiceMaskComponent, ClothingGotEquippedEvent>(OnEquip);
        SubscribeLocalEvent<VoiceMaskSetNameEvent>(OpenUI);
        SubscribeLocalEvent<VoiceMaskComponent, TransformSpeechEvent>(OnTransformSpeech, before: [typeof(AccentSystem)]);
        SubscribeLocalEvent<VoiceMaskComponent, InventoryRelayedEvent<TransformSpeechEvent>>(OnTransformSpeechInventory, before: [typeof(AccentSystem)]);
        SubscribeLocalEvent<VoiceMaskComponent, ImplantRelayEvent<TransformSpeechEvent>>(OnTransformSpeechImplant, before: [typeof(AccentSystem)]);
        SubscribeLocalEvent<VoiceMaskComponent, VoiceMaskChangeBarksMessage>(OnChangeBarks);
        SubscribeLocalEvent<VoiceMaskComponent, ClothingGotUnequippedEvent>(OnUnequip);

        InitializeTTS(); // CorvaxGoob-TTS

        Subs.CVar(_cfgManager, CCVars.MaxNameLength, value => _maxNameLength = value, true);
    }

    /// <summary>
    ///     Hides accent if the voice mask is on and the option to block accents is on
    /// </summary>
    private void TransformSpeech(Entity<VoiceMaskComponent> entity, TransformSpeechEvent args)
    {
        if (entity.Comp.AccentHide && entity.Comp.Active)
            args.Cancel();
        if (entity.Comp.Active && entity.Comp.VoiceBarkPrototypeId != null)
        {
            if (EntityManager.TryGetComponent<VoiceBarkComponent>(args.Sender, out var barkComp))
            {
                barkComp.VoiceId = entity.Comp.VoiceBarkPrototypeId;

                if (entity.Comp.VoiceBarkPitch.HasValue)
                    barkComp.BasePitch = entity.Comp.VoiceBarkPitch.Value;
                if (entity.Comp.VoiceBarkPitchVar.HasValue)
                    barkComp.PitchVariation = entity.Comp.VoiceBarkPitchVar.Value;
                Dirty(args.Sender, barkComp);
            }
        }
    }

    private void OnTransformSpeech(Entity<VoiceMaskComponent> entity, ref TransformSpeechEvent args)
    {
        TransformSpeech(entity, args);
    }

    private void OnTransformSpeechInventory(Entity<VoiceMaskComponent> entity, ref InventoryRelayedEvent<TransformSpeechEvent> args)
    {
        TransformSpeech(entity, args.Args);
    }

    private void OnTransformSpeechImplant(Entity<VoiceMaskComponent> entity, ref ImplantRelayEvent<TransformSpeechEvent> args)
    {
        TransformSpeech(entity, args.Event);
    }

    private void OnTransformSpeakerNameInventory(Entity<VoiceMaskComponent> entity, ref InventoryRelayedEvent<TransformSpeakerNameEvent> args)
    {
        TransformVoice(entity, args.Args);
    }

    private void OnTransformSpeakerNameImplant(Entity<VoiceMaskComponent> entity, ref ImplantRelayEvent<TransformSpeakerNameEvent> args)
    {
        TransformVoice(entity, args.Event);
    }

    private void OnSeeIdentityAttemptEvent(Entity<VoiceMaskComponent> entity, ref ImplantRelayEvent<SeeIdentityAttemptEvent> args)
    {
        if (!entity.Comp.OverrideIdentity || !entity.Comp.Active)
            return;

        args.Event.NameOverride = GetCurrentVoiceName(entity);
    }

    private void OnImplantImplantedEvent(Entity<VoiceMaskComponent> entity, ref ImplantImplantedEvent ev)
    {
        _identity.QueueIdentityUpdate(ev.Implanted);
    }

    private void OnImplantRemovedEventEvent(Entity<VoiceMaskComponent> entity, ref ImplantRemovedEvent ev)
    {
        _identity.QueueIdentityUpdate(ev.Implanted);
    }

    private void OnLockToggled(Entity<VoiceMaskComponent> ent, ref LockToggledEvent args)
    {
        if (args.Locked)
            _actions.RemoveAction(ent.Comp.ActionEntity);
        else if (_container.TryGetContainingContainer(ent.Owner, out var container))
            _actions.AddAction(container.Owner, ref ent.Comp.ActionEntity, ent.Comp.Action, ent);
    }

    #region User inputs from UI
    private void OnChangeVerb(Entity<VoiceMaskComponent> entity, ref VoiceMaskChangeVerbMessage msg)
    {
        if (msg.Verb is { } id && !_proto.HasIndex<SpeechVerbPrototype>(id))
            return;

        entity.Comp.VoiceMaskSpeechVerb = msg.Verb;
        // verb is only important to metagamers so no need to log as opposed to name

        _popupSystem.PopupEntity(Loc.GetString("voice-mask-popup-success"), entity, msg.Actor);

        UpdateUI(entity);
    }

    private void OnChangeName(Entity<VoiceMaskComponent> entity, ref VoiceMaskChangeNameMessage message)
    {
        if (message.Name.Length > _maxNameLength || message.Name.Length <= 0)
        {
            _popupSystem.PopupEntity(Loc.GetString("voice-mask-popup-failure"), entity, message.Actor, PopupType.SmallCaution);
            return;
        }

        var nameUpdatedEvent = new VoiceMaskNameUpdatedEvent(entity, entity.Comp.VoiceMaskName, message.Name);
        RaiseLocalEvent(message.Actor, ref nameUpdatedEvent);

        entity.Comp.VoiceMaskName = message.Name;
        _adminLogger.Add(LogType.Action, LogImpact.Medium, $"{ToPrettyString(message.Actor):player} set voice of {ToPrettyString(entity):mask}: {entity.Comp.VoiceMaskName}");

        _popupSystem.PopupEntity(Loc.GetString("voice-mask-popup-success"), entity, message.Actor);

        UpdateUI(entity);
    }

    private void OnToggle(Entity<VoiceMaskComponent> entity, ref VoiceMaskToggleMessage args)
    {
        _popupSystem.PopupEntity(Loc.GetString("voice-mask-popup-toggle"), entity, args.Actor);
        entity.Comp.Active = !entity.Comp.Active;
        if (!entity.Comp.Active && entity.Comp.HasBackup && TryComp<VoiceBarkComponent>(args.Actor, out var barkComp))
        {
            barkComp.VoiceId = entity.Comp.OriginalVoiceId;
            barkComp.BasePitch = entity.Comp.OriginalBasePitch;
            barkComp.PitchVariation = entity.Comp.OriginalPitchVariation;

            entity.Comp.OriginalVoiceId = null;
            entity.Comp.HasBackup = false;
        }
        // Update identity because of possible name override
        _identity.QueueIdentityUpdate(args.Actor);
    }

    private void OnAccentToggle(Entity<VoiceMaskComponent> entity, ref VoiceMaskAccentToggleMessage args)
    {
        _popupSystem.PopupEntity(Loc.GetString("voice-mask-popup-accent-toggle"), entity, args.Actor);
        entity.Comp.AccentHide = !entity.Comp.AccentHide;
    }
    private void OnChangeBarks(Entity<VoiceMaskComponent> entity, ref VoiceMaskChangeBarksMessage message)
    {
        if (EntityManager.TryGetComponent<VoiceBarkComponent>(message.Actor, out var barkComp))
        {
            barkComp.VoiceId = message.BarkVoiceId;
            barkComp.BasePitch = message.BarkPitch;
            barkComp.PitchVariation = message.BarkPitchVar;
            Dirty(message.Actor, barkComp);

            entity.Comp.VoiceBarkPrototypeId = message.BarkVoiceId;
            entity.Comp.VoiceBarkPitch = message.BarkPitch;
            entity.Comp.VoiceBarkPitchVar = message.BarkPitchVar;
        }
    }



    #endregion

    #region UI
    private void OnEquip(EntityUid uid, VoiceMaskComponent component, ClothingGotEquippedEvent args)
    {
        if (_lock.IsLocked(uid))
            return;

        if (component.EnableAction) //Goobstation
            _actions.AddAction(args.Wearer, ref component.ActionEntity, component.Action, uid);
        if (!component.HasBackup && TryComp<VoiceBarkComponent>(args.Wearer, out var barkComp))
        {
            component.OriginalVoiceId = barkComp.VoiceId;
            component.OriginalBasePitch = barkComp.BasePitch;
            component.OriginalPitchVariation = barkComp.PitchVariation;
            component.HasBackup = true;
        }
    }
    private void OnUnequip(EntityUid uid, VoiceMaskComponent component, ClothingGotUnequippedEvent args)
    {
        if (component.EnableAction)
            _actions.RemoveAction(component.ActionEntity);
        if (component.HasBackup && TryComp<VoiceBarkComponent>(args.Wearer, out var barkComp))
        {
            barkComp.VoiceId = component.OriginalVoiceId;
            barkComp.BasePitch = component.OriginalBasePitch;
            barkComp.PitchVariation = component.OriginalPitchVariation;
            Dirty(args.Wearer, barkComp);
            component.OriginalVoiceId = null;
            component.HasBackup = false;
        }
    }


    private void OpenUI(VoiceMaskSetNameEvent ev)
    {
        var maskEntity = ev.Action.Comp.Container;

        if (!TryComp<VoiceMaskComponent>(maskEntity, out var voiceMaskComp))
            return;

        if (!_uiSystem.HasUi(maskEntity.Value, VoiceMaskUIKey.Key))
            return;

        _uiSystem.OpenUi(maskEntity.Value, VoiceMaskUIKey.Key, ev.Performer);
        UpdateUI((maskEntity.Value, voiceMaskComp));
    }

    public void UpdateUI(Entity<VoiceMaskComponent> entity) // Make public by goobstation
    {
        if (_uiSystem.HasUi(entity, VoiceMaskUIKey.Key))
        {
            _uiSystem.SetUiState(entity.Owner, VoiceMaskUIKey.Key, new VoiceMaskBuiState(
                GetCurrentVoiceName(entity),
                entity.Comp.VoiceMaskSpeechVerb,
                entity.Comp.Active,
                entity.Comp.AccentHide,
                entity.Comp.JobIconProtoId,
                entity.Comp.VoiceId,
                entity.Comp.VoiceBarkPrototypeId,
                entity.Comp.VoiceBarkPitch ?? 1.0f,
                entity.Comp.VoiceBarkPitchVar ?? 0.0f
            ));
        }
    }

    #endregion

    #region Helper functions
    private string GetCurrentVoiceName(Entity<VoiceMaskComponent> entity)
    {
        return entity.Comp.VoiceMaskName ?? Loc.GetString("voice-mask-default-name-override");
    }

    private void TransformVoice(Entity<VoiceMaskComponent> entity, TransformSpeakerNameEvent args)
    {
        if (!entity.Comp.Active)
            return;

        args.VoiceName = GetCurrentVoiceName(entity);
        args.SpeechVerb = entity.Comp.VoiceMaskSpeechVerb ?? args.SpeechVerb;
    }
    #endregion
}
