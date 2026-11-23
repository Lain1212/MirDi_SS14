using System.Numerics;
using Content.Shared._WorldDi;
using Content.Shared.Body.Components;
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
    private static readonly Color AdaptationColor = Color.FromHex("#8899BB");
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(0.5);
    private const float MaxLightSearchRange = 10f;

    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly PointLightSystem _lights = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedBodySystem _body = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private EntityQuery<TransformComponent> _xformQuery;
    private EntityQuery<DarkAdaptationComponent> _adaptationQuery;
    private EntityQuery<BodyComponent> _bodyQuery;

    private readonly HashSet<Entity<PointLightComponent>> _nearbyLights = new();

    private EntityUid? _cachedBody;
    private EntityUid? _cachedEyes;
    private TimeSpan _nextOrganCheck;
    private float _lastProgress = -1f;

    public override void Initialize()
    {
        base.Initialize();

        _xformQuery = GetEntityQuery<TransformComponent>();
        _adaptationQuery = GetEntityQuery<DarkAdaptationComponent>();
        _bodyQuery = GetEntityQuery<BodyComponent>();

        SubscribeLocalEvent<DarkAdaptationComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnShutdown(Entity<DarkAdaptationComponent> ent, ref ComponentShutdown args)
    {
        CleanupLight(ent.Comp);

        if (_cachedEyes == ent.Owner)
        {
            _cachedEyes = null;
            _lastProgress = -1f;
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_timing.IsFirstTimePredicted)
            return;

        if (_player.LocalEntity is not { } playerUid)
        {
            CleanupCachedEyes();
            return;
        }

        var curTime = _timing.CurTime;
        var prevEyes = _cachedEyes;

        if (!TryGetCachedEyes(playerUid, curTime, out var comp))
        {
            if (prevEyes != null)
                CleanupEyes(prevEyes.Value);

            return;
        }

        if (prevEyes != null && prevEyes != _cachedEyes)
            CleanupEyes(prevEyes.Value);

        if (!_xformQuery.TryGetComponent(playerUid, out var xform))
            return;

        var isInDarkness = IsPlayerInDarkness(playerUid, xform, curTime, comp);

        if (isInDarkness)
        {
            var maxTime = comp.DelayBeforeAdaptation + 5f;
            comp.TimeInDark = Math.Min(maxTime, comp.TimeInDark + frameTime);
        }
        else
        {
            comp.TimeInDark = Math.Max(0f, comp.TimeInDark - frameTime * 4f);
        }

        var progress = Math.Clamp((comp.TimeInDark - comp.DelayBeforeAdaptation) / 5f, 0f, 1f);
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
            _lastProgress = -1f;
        }

        if (curTime >= _nextOrganCheck)
        {
            _nextOrganCheck = curTime + CheckInterval;
            _cachedEyes = null;

            if (_adaptationQuery.TryGetComponent(bodyUid, out comp!))
            {
                _cachedEyes = bodyUid;
                return true;
            }

            if (!_bodyQuery.HasComponent(bodyUid))
                return false;

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

    private bool IsPlayerInDarkness(
        EntityUid playerUid,
        TransformComponent xform,
        TimeSpan curTime,
        DarkAdaptationComponent comp)
    {
        if (curTime < comp.NextCheck)
            return comp.IsDark;

        comp.NextCheck = curTime + CheckInterval;

        _nearbyLights.Clear();
        _lookup.GetEntitiesInRange(xform.Coordinates, MaxLightSearchRange, _nearbyLights);

        if (_nearbyLights.Count == 0)
        {
            comp.IsDark = true;
            return true;
        }

        var playerWorldPos = _transform.GetWorldPosition(xform);

        foreach (var lightEnt in _nearbyLights)
        {
            if (lightEnt.Owner == comp.LightEntity)
                continue;

            var light = lightEnt.Comp;

            if (!light.Enabled || light.Energy < 0.5f || light.Radius < 1.5f)
                continue;

            if (lightEnt.Owner == playerUid)
            {
                comp.IsDark = false;
                return false;
            }

            if (!_xformQuery.TryGetComponent(lightEnt.Owner, out var lightXform))
                continue;

            if (lightXform.ParentUid == playerUid)
            {
                comp.IsDark = false;
                return false;
            }

            var effectiveRange = light.Radius * 0.85f;
            var lightWorldPos = _transform.GetWorldPosition(lightXform);

            if (Vector2.DistanceSquared(playerWorldPos, lightWorldPos) > effectiveRange * effectiveRange)
                continue;

            if (_interaction.InRangeUnobstructed(
                    playerUid,
                    lightEnt.Owner,
                    range: effectiveRange,
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
            if (comp.LightEntity != null)
                CleanupLight(comp);

            _lastProgress = 0f;
            return;
        }

        var spawnedNew = false;

        if (!Exists(comp.LightEntity))
        {
            var lightUid = Spawn(null, new EntityCoordinates(playerUid, 0, 0));
            _transform.SetParent(lightUid, playerUid);
            comp.LightEntity = lightUid;
            spawnedNew = true;
        }

        if (!spawnedNew && MathF.Abs(progress - _lastProgress) < 0.001f)
            return;

        _lastProgress = progress;

        var lightUidVal = comp.LightEntity.Value;
        var light = EnsureComp<PointLightComponent>(lightUidVal);

        if (spawnedNew)
        {
            _lights.SetEnabled(lightUidVal, true, light);
            _lights.SetColor(lightUidVal, AdaptationColor, light);
        }

        _lights.SetRadius(lightUidVal, comp.MaxRadius * progress, light);
        _lights.SetEnergy(lightUidVal, comp.MaxEnergy * progress, light);
    }

    private void CleanupLight(DarkAdaptationComponent comp)
    {
        if (Exists(comp.LightEntity))
            QueueDel(comp.LightEntity.Value);

        comp.LightEntity = null;
        _lastProgress = -1f;
    }

    private void CleanupCachedEyes()
    {
        if (_cachedEyes is { } oldEyes)
            CleanupEyes(oldEyes);

        _cachedEyes = null;
        _cachedBody = null;
        _lastProgress = -1f;
    }

    private void CleanupEyes(EntityUid eyesUid)
    {
        if (_adaptationQuery.TryGetComponent(eyesUid, out var oldComp))
        {
            CleanupLight(oldComp);
            oldComp.TimeInDark = 0f;
            oldComp.IsDark = false;
        }
    }
}
