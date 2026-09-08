using Robust.Shared;
using Robust.Shared.Configuration;

namespace Content.Shared._WorldDi;

[CVarDefs]
public sealed partial class CCVars : CVars
{
    /// <summary>
    /// Полностью включает или отключает звук барков персонажей.
    /// </summary>
    public static readonly CVarDef<bool> ChatVoiceBarksEnabled =
        CVarDef.Create("chat.voice_barks_enabled", true, CVar.CLIENT | CVar.ARCHIVE);

    /// <summary>
    /// Полностью включает или отключает посимвольную анимацию появления текста.
    /// </summary>
    public static readonly CVarDef<bool> ChatTextAnimationEnabled =
        CVarDef.Create("chat.text_animation_enabled", true, CVar.CLIENT | CVar.ARCHIVE);
}
