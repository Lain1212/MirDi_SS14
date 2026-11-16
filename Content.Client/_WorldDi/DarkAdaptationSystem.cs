using Content.Shared._WorldDi;
using Content.Shared.Body.Systems;
using Content.Shared.Interaction;
using Content.Shared.Physics;
using Robust.Client.GameObjects;
using Robust.Client.Player;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Client._WorldDi;

public sealed class DarkAdaptationSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly PointLightSystem _lights = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedBodySystem _body = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private EntityQuery<TransformComponent> _xformQuery;
    private EntityQuery<DarkAdaptationComponent> _adaptationQuery;

    private readonly HashSet<Entity<PointLightComponent>> _nearbyLights = new();

    private EntityUid? _cachedBody;
    private EntityUid? _cachedEyes;
    private TimeSpan _nextOrganCheck;

    public override void Initialize()
    {
        base.Initialize();
        _xformQuery = GetEntityQuery<TransformComponent>();
        _adaptationQuery = GetEntityQuery<DarkAdaptationComponent>();

        SubscribeLocalEvent<DarkAdaptationComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnShutdown(Entity<DarkAdaptationComponent> ent, ref ComponentShutdown args)
    {
        CleanupLight(ent.Comp);

        if (_cachedEyes == ent.Owner)
            _cachedEyes = null;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_timing.IsFirstTimePredicted)
            return;

        if (_player.LocalEntity is not { } playerUid)
            return;

        var curTime = _timing.CurTime;
        var prevEyes = _cachedEyes;
        if (!TryGetCachedEyes(playerUid, curTime, out var comp))
        {
            if (prevEyes is { } oldEyes && _adaptationQuery.TryGetComponent(oldEyes, out var oldComp))
                CleanupLight(oldComp);
            return;
        }


        if (!_xformQuery.TryGetComponent(playerUid, out var xform))
            return;

        bool isInDarkness = IsPlayerInDarkness(playerUid, xform, curTime, comp);

        if (isInDarkness)
        {
            float maxTime = comp.DelayBeforeAdaptation + 5f;
            comp.TimeInDark = Math.Min(maxTime, comp.TimeInDark + frameTime);
        }
        else
        {
            comp.TimeInDark = Math.Max(0f, comp.TimeInDark - frameTime * 4f);
        }

        float progress = Math.Clamp((comp.TimeInDark - comp.DelayBeforeAdaptation) / 5f, 0f, 1f);

        UpdatePersonalLight(playerUid, comp, progress);
    }

    private bool TryGetCachedEyes(EntityUid bodyUid, TimeSpan curTime, out DarkAdaptationComponent comp)
    {
        comp = default!;

        if (_cachedBody != bodyUid)
        {
            _cachedBody = bodyUid;
            _cachedEyes = null;
            _nextOrganCheck = TimeSpan.Zero;
        }

        if (curTime >= _nextOrganCheck)
        {
            _nextOrganCheck = curTime + TimeSpan.FromSeconds(0.5);
            _cachedEyes = null;

            foreach (var organ in _body.GetBodyOrganEntityComps<DarkAdaptationComponent>(bodyUid))
            {
                _cachedEyes = organ.Owner;
                comp = organ.Comp1;
                return true;
            }

            return false;
        }

        return _cachedEyes is { } eyesUid && _adaptationQuery.TryGetComponent(eyesUid, out comp!);
    }

    private bool IsPlayerInDarkness(EntityUid playerUid, TransformComponent xform, TimeSpan curTime, DarkAdaptationComponent comp)
    {
        if (curTime < comp.NextCheck)
            return comp.IsDark;

        comp.NextCheck = curTime + TimeSpan.FromSeconds(0.5);

        _nearbyLights.Clear();
        _lookup.GetEntitiesInRange(xform.Coordinates, 10f, _nearbyLights);

        foreach (var lightEnt in _nearbyLights)
        {
            if (lightEnt.Owner == comp.LightEntity)
                continue;

            var light = lightEnt.Comp;

            if (!light.Enabled || light.Energy < 0.5f || light.Radius < 1.5f)
                continue;

            if (lightEnt.Owner == playerUid ||
                (_xformQuery.TryGetComponent(lightEnt.Owner, out var lightXform) && lightXform.ParentUid == playerUid))
            {
                comp.IsDark = false;
                return false;
            }

            if (_interaction.InRangeUnobstructed(
                playerUid,
                lightEnt.Owner,
                range: light.Radius * 0.85f,
                collisionMask: CollisionGroup.Opaque))
            {
                comp.IsDark = false;
                return false;
            }
        }

        comp.IsDark = true;
        return true;
    }

    private void UpdatePersonalLight(EntityUid playerUid, DarkAdaptationComponent comp, float progress)
    {
        if (progress <= 0f)
        {
            CleanupLight(comp);
            return;
        }

        if (!Exists(comp.LightEntity))
        {
            var lightUid = Spawn(null, new EntityCoordinates(playerUid, 0, 0));
            _transform.SetParent(lightUid, playerUid);
            comp.LightEntity = lightUid;
        }

        var light = EnsureComp<PointLightComponent>(comp.LightEntity.Value);

        _lights.SetEnabled(comp.LightEntity.Value, true, light);
        _lights.SetRadius(comp.LightEntity.Value, comp.MaxRadius * progress, light);
        _lights.SetEnergy(comp.LightEntity.Value, comp.MaxEnergy * progress, light);
        _lights.SetColor(comp.LightEntity.Value, Color.FromHex("#8899BB"), light);
    }

    private void CleanupLight(DarkAdaptationComponent comp)
    {
        if (Exists(comp.LightEntity))
        {
            QueueDel(comp.LightEntity.Value);
        }

        comp.LightEntity = null;
    }
}
