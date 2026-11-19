// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Shared.Speech;
using Robust.Shared.Prototypes;

namespace Content.Client.VoiceMask;

public sealed partial class VoiceMaskNameChangeWindow
{
    public Action<string?, float, float>? OnBarksChange;

    private void InitializeBarks()
    {
        var protoManager = IoCManager.Resolve<IPrototypeManager>();

        var barkVoices = protoManager.EnumeratePrototypes<VoicePrototype>()
            .OrderBy(o => o.ID)
            .ToList();

        BarkVoiceSelector.AddItem(Loc.GetString("chat-speech-verb-name-none"));
        BarkVoiceSelector.SetItemMetadata(0, "");

        for (var i = 0; i < barkVoices.Count; i++)
        {
            var id = barkVoices[i].ID;
            var displayName = Loc.GetString($"voice-id-{id}");
            var nextId = BarkVoiceSelector.ItemCount;
            BarkVoiceSelector.AddItem(displayName, nextId);
            BarkVoiceSelector.SetItemMetadata(nextId, id);
        }

        BarkVoiceSelector.OnItemSelected += args =>
        {
            BarkVoiceSelector.SelectId(args.Id);
            SendBarkUpdate();
        };

        BarkPitchSlider.OnValueChanged += _ =>
        {
            BarkPitchLabel.Text = BarkPitchSlider.Value.ToString("F2");
            SendBarkUpdate();
        };

        BarkPitchVarSlider.OnValueChanged += _ =>
        {
            BarkPitchVarLabel.Text = BarkPitchVarSlider.Value.ToString("F2");
            SendBarkUpdate();
        };
    }

    private void SendBarkUpdate()
    {
        var selectedId = BarkVoiceSelector.SelectedId;
        var selectedProto = BarkVoiceSelector.GetItemMetadata(selectedId) as string;
        OnBarksChange?.Invoke(selectedProto, BarkPitchSlider.Value, BarkPitchVarSlider.Value);
    }

    private void UpdateBarksState(string? barkVoiceId, float barkPitch, float barkPitchVar)
    {
        BarkPitchSlider.SetValueWithoutEvent(barkPitch);
        BarkPitchVarSlider.SetValueWithoutEvent(barkPitchVar);
        BarkPitchLabel.Text = barkPitch.ToString("F2");
        BarkPitchVarLabel.Text = barkPitchVar.ToString("F2");

        for (var id = 0; id < BarkVoiceSelector.ItemCount; id++)
        {
            if (string.Equals(barkVoiceId, BarkVoiceSelector.GetItemMetadata(id) as string))
            {
                BarkVoiceSelector.SelectId(id);
                break;
            }
        }
    }
}
