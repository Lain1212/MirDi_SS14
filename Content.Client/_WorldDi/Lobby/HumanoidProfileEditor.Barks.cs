// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Shared.Speech;
using Robust.Client.Audio;
using Robust.Shared.Audio;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    private static readonly float[] BarksTimeline = [0.00f, 0.05f, 0.10f, 0.15f, 0.20f, 0.55f, 0.60f, 0.65f];
    private static readonly TimeSpan PlaybackDebounceTime = TimeSpan.FromSeconds(0.75f);

    private bool _barksInitialized;

    private void InitializeVoiceBarkSettings()
    {
        if (_barksInitialized)
            return;

        _barksInitialized = true;
        VoiceBarkOptionButton.Clear();

        var barks = _prototypeManager.EnumeratePrototypes<VoicePrototype>()
            .Where(b => b.Roundstart)
            .ToList();

        for (var i = 0; i < barks.Count; i++)
        {
            var displayName = Loc.GetString($"voice-id-{barks[i].ID}");
            VoiceBarkOptionButton.AddItem(displayName, i);
            VoiceBarkOptionButton.SetItemMetadata(i, barks[i].ID);
        }

        VoiceBarkPitchSlider.OnValueChanged += args =>
        {
            VoiceBarkPitchLabel.Text = args.Value.ToString("F2");
            if (Profile == null)
                return;

            Profile = Profile.WithVoiceBarkPitch(args.Value, (float) VoiceBarkPitchVarSlider.Value);
            SetDirty();
        };

        VoiceBarkPitchVarSlider.OnValueChanged += args =>
        {
            VoiceBarkPitchVarLabel.Text = args.Value.ToString("F2");
            if (Profile == null)
                return;

            Profile = Profile.WithVoiceBarkPitch((float) VoiceBarkPitchSlider.Value, args.Value);
            SetDirty();
        };

        VoiceBarkOptionButton.OnItemSelected += args =>
        {
            VoiceBarkOptionButton.SelectId(args.Id);
            if (VoiceBarkOptionButton.GetItemMetadata(args.Id) is string voiceId)
            {
                Profile = Profile?.WithVoiceBarkId(voiceId);
                SetDirty();
            }
        };

        VoiceBarkPlayButton.OnPressed += _ => PlayBarkPreview();
    }

    private void PlayBarkPreview()
    {
        if (VoiceBarkOptionButton.SelectedMetadata is not string voiceId)
            return;

        if (!_prototypeManager.TryIndex<VoicePrototype>(voiceId, out var voiceProto) || voiceProto.Sounds.Count == 0)
            return;

        var random = IoCManager.Resolve<IRobustRandom>();
        var audioSystem = _entManager.System<AudioSystem>();

        VoiceBarkPlayButton.Disabled = true;

        void PlayLetter()
        {
            if (Disposed)
                return;

            var sound = random.Pick(voiceProto.Sounds);
            var basePitch = (float) VoiceBarkPitchSlider.Value;
            var pitchVar = (float) VoiceBarkPitchVarSlider.Value;
            var finalPitch = Math.Max(0.2f, basePitch + random.NextFloat(-pitchVar, pitchVar));

            var audioParams = AudioParams.Default with { Pitch = finalPitch, Volume = voiceProto.Volume };
            audioSystem.PlayGlobal(sound, Filter.Local(), false, audioParams);
        }

        foreach (var delay in BarksTimeline)
        {
            Timer.Spawn(TimeSpan.FromSeconds(delay), PlayLetter);
        }

        Timer.Spawn(PlaybackDebounceTime, () =>
        {
            if (!Disposed)
                VoiceBarkPlayButton.Disabled = false;
        });
    }

    private void UpdateVoiceBarkControls()
    {
        if (Profile == null)
            return;

        if (!_barksInitialized)
            InitializeVoiceBarkSettings();

        VoiceBarkPitchSlider.SetValueWithoutEvent(Profile.VoiceBarkPitch);
        VoiceBarkPitchLabel.Text = Profile.VoiceBarkPitch.ToString("F2");

        VoiceBarkPitchVarSlider.SetValueWithoutEvent(Profile.VoiceBarkPitchVar);
        VoiceBarkPitchVarLabel.Text = Profile.VoiceBarkPitchVar.ToString("F2");

        var matched = false;
        for (var i = 0; i < VoiceBarkOptionButton.ItemCount; i++)
        {
            if (VoiceBarkOptionButton.GetItemMetadata(i) is string voiceId && voiceId == Profile.VoiceBarkId)
            {
                VoiceBarkOptionButton.SelectId(i);
                matched = true;
                break;
            }
        }

        if (!matched && VoiceBarkOptionButton.ItemCount > 0)
            VoiceBarkOptionButton.SelectId(0);
    }
}
