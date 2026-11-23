using Robust.Shared.Audio;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Manager.Attributes;
using System.Collections.Generic;

namespace Content.Shared.Speech;


[Prototype("voice")]
public sealed partial class VoicePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField("sounds", required: true)]
    public List<SoundSpecifier> Sounds { get; private set; } = new();

    [DataField("basePitch")]
    public float BasePitch { get; private set; } = 1.0f;

    [DataField("pitchVariation")]
    public float PitchVariation { get; private set; } = 0.05f;

    [DataField("volume")]
    public float Volume { get; private set; } = 0.0f;

    [DataField("roundstart")]
    public bool Roundstart { get; private set; } = true;

}




[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class VoiceBarkComponent : Component
{
    [DataField("voiceId"), AutoNetworkedField]
    public string? VoiceId { get; set; } = "DefaultVoice";

    [DataField("basePitch"), AutoNetworkedField]
    public float BasePitch { get; set; } = 1.0f;

    [DataField("pitchVariation"), AutoNetworkedField]
    public float PitchVariation { get; set; } = 0.0f;
}
/*
[Serializable, NetSerializable]
public sealed class VoiceBarkComponentState : ComponentState
{
    public string? VoiceId { get; }
    public float BasePitch { get; }
    public float PitchVariation { get; }

    public VoiceBarkComponentState(string? voiceId, float basePitch, float pitchVariation)
    {
        VoiceId = voiceId;
        BasePitch = basePitch;
        PitchVariation = pitchVariation;
    }
}
*/
