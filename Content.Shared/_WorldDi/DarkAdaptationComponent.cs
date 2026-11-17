using Robust.Shared.GameStates;

namespace Content.Shared._WorldDi;

[RegisterComponent, NetworkedComponent]
public sealed partial class DarkAdaptationComponent : Component
{
    public float TimeInDark;
    public bool IsDark;
    public TimeSpan NextCheck;
    public EntityUid? LightEntity;

    [DataField]
    public float DelayBeforeAdaptation = 10f;

    [DataField]
    public float MaxRadius = 2.5f;

    [DataField]
    public float MaxEnergy = 0.2f;
}
