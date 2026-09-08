using Robust.Shared;
using Robust.Shared.Configuration;

namespace Content.Shared._WorldDi.CCVar;

[CVarDefs]
public sealed partial class СCVarsPP : CVars
{
    /// <summary>
    /// WolrdDi - Включение и выключение пост-обработки
    /// </summary>
    public static readonly CVarDef<bool> PostProcessBloomEnabled =
        CVarDef.Create("post_process.bloom_enabled", true, CVar.CLIENT | CVar.ARCHIVE);
}
