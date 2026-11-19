// SPDX-License-Identifier: MIT

using Robust.Shared.Serialization;

namespace Content.Shared.VoiceMask;

[Serializable, NetSerializable]
public sealed class VoiceMaskChangeBarksMessage : BoundUserInterfaceMessage
{
    public readonly string? BarkVoiceId;
    public readonly float BarkPitch;
    public readonly float BarkPitchVar;

    public VoiceMaskChangeBarksMessage(string? barkVoiceId, float barkPitch, float barkPitchVar)
    {
        BarkVoiceId = barkVoiceId;
        BarkPitch = barkPitch;
        BarkPitchVar = barkPitchVar;
    }
}
