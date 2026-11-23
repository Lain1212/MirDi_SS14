// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Clothing;
using Content.Shared.Speech;
using Content.Shared.VoiceMask;

namespace Content.Server.VoiceMask;

public sealed partial class VoiceMaskSystem
{
    private void InitializeBarks()
    {
        SubscribeLocalEvent<VoiceMaskComponent, VoiceMaskChangeBarksMessage>(OnChangeBarks);
        SubscribeLocalEvent<VoiceMaskComponent, ClothingGotUnequippedEvent>(OnUnequipBarks);
    }

    private void ApplyMaskBarksOnSpeech(Entity<VoiceMaskComponent> entity, EntityUid speaker)
    {
        if (!entity.Comp.Active || entity.Comp.VoiceBarkPrototypeId == null)
            return;

        if (!TryComp<VoiceBarkComponent>(speaker, out var barkComp))
            return;

        barkComp.VoiceId = entity.Comp.VoiceBarkPrototypeId;

        if (entity.Comp.VoiceBarkPitch.HasValue)
            barkComp.BasePitch = entity.Comp.VoiceBarkPitch.Value;

        if (entity.Comp.VoiceBarkPitchVar.HasValue)
            barkComp.PitchVariation = entity.Comp.VoiceBarkPitchVar.Value;

        Dirty(speaker, barkComp);
    }

    private void BackupBarksOnEquip(EntityUid wearer, VoiceMaskComponent component)
    {
        if (component.HasBackup || !TryComp<VoiceBarkComponent>(wearer, out var barkComp))
            return;

        component.OriginalVoiceId = barkComp.VoiceId;
        component.OriginalBasePitch = barkComp.BasePitch;
        component.OriginalPitchVariation = barkComp.PitchVariation;
        component.HasBackup = true;
    }

    private void RestoreBarks(EntityUid wearer, VoiceMaskComponent component)
    {
        if (!component.HasBackup || !TryComp<VoiceBarkComponent>(wearer, out var barkComp))
            return;

        barkComp.VoiceId = component.OriginalVoiceId;
        barkComp.BasePitch = component.OriginalBasePitch;
        barkComp.PitchVariation = component.OriginalPitchVariation;
        Dirty(wearer, barkComp);

        component.OriginalVoiceId = null;
        component.HasBackup = false;
    }

    private void OnUnequipBarks(EntityUid uid, VoiceMaskComponent component, ClothingGotUnequippedEvent args)
    {
        RestoreBarks(args.Wearer, component);
    }

    private void OnChangeBarks(Entity<VoiceMaskComponent> entity, ref VoiceMaskChangeBarksMessage message)
    {
        if (TryComp<VoiceBarkComponent>(message.Actor, out var barkComp))
        {
            barkComp.VoiceId = message.BarkVoiceId;
            barkComp.BasePitch = message.BarkPitch;
            barkComp.PitchVariation = message.BarkPitchVar;
            Dirty(message.Actor, barkComp);
        }

        entity.Comp.VoiceBarkPrototypeId = message.BarkVoiceId;
        entity.Comp.VoiceBarkPitch = message.BarkPitch;
        entity.Comp.VoiceBarkPitchVar = message.BarkPitchVar;
    }
}
