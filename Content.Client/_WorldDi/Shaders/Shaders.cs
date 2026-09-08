using Content.Client.Overlays;
using Content.Shared._WorldDi.CCVar;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;
using System.Numerics;

namespace Content.Client._WorldDi.Shaders;

public sealed class BloomSystem : EntitySystem
{
    [Dependency] private readonly IOverlayManager _overlayManager = default!;

    private BloomOverlay? _overlay;

    public override void Initialize()
    {
        base.Initialize();

        _overlay = new BloomOverlay();
        _overlayManager.AddOverlay(_overlay);
    }

    public override void Shutdown()
    {
        base.Shutdown();

        if (_overlay != null)
        {
            _overlayManager.RemoveOverlay(_overlay);
            _overlay.Dispose();
            _overlay = null;
        }
    }
}

public sealed partial class BloomOverlay : Overlay
{
    private static readonly ProtoId<ShaderPrototype> ShaderId = "BloomShader";

    [Dependency] private readonly IConfigurationManager _configManager = default!;
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;

    public override bool RequestScreenTexture => true;
    public override OverlaySpace Space => OverlaySpace.WorldSpace;
    private readonly ShaderInstance _bloomShader;

    public BloomOverlay()
    {
        IoCManager.InjectDependencies(this);
        _bloomShader = _prototypeManager.Index(ShaderId).InstanceUnique();
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {

        if (!_configManager.GetCVar(СCVarsPP.PostProcessBloomEnabled))
            return false;

        if (!_entityManager.TryGetComponent(_playerManager.LocalSession?.AttachedEntity, out EyeComponent? eyeComp))
            return false;

        if (args.Viewport.Eye != eyeComp.Eye)
            return false;

        return true;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (ScreenTexture == null)
            return;

        if (args.Viewport.LightRenderTarget?.Texture == null)
            return;

        var worldHandle = args.WorldHandle;
        var viewport = args.WorldBounds;

        _bloomShader.SetParameter("SCREEN_TEXTURE", ScreenTexture);
        _bloomShader.SetParameter("LIGHT_TEXTURE", args.Viewport.LightRenderTarget.Texture);

        var zoom = args.Viewport.Eye?.Zoom.X ?? 1.0f;
        _bloomShader.SetParameter("Zoom", zoom);

        worldHandle.UseShader(_bloomShader);
        worldHandle.DrawRect(viewport, Color.White);
        worldHandle.UseShader(null);
    }
}
