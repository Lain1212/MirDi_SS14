using Content.Server.Chat.Managers;
using Content.Server.GameTicking;
using Content.Shared._CorvaxGoob.CCCVars;
using Content.Shared.Bed.Cryostorage;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.Mind;
using Content.Shared.Roles;
using Content.Shared.Roles.Components;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.Timing;

namespace Content.Server._CorvaxGoob.Skills;

/// <summary>
/// Automatically turns skills off while the station crew is too small,
/// and turns them back on when enough people are on the manifest again.
/// </summary>
public sealed partial class SkillsSystem
{
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly SharedRoleSystem _roles = default!;
    [Dependency] private readonly GameTicker _ticker = default!;

    /// <summary>
    /// How often the crew count is recalculated.
    /// </summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);

    private TimeSpan _nextCheck;

    private bool _autoDisableEnabled;
    private int _autoDisableThreshold;
    private int _autoDisableHysteresis;

    /// <summary>
    /// True while skills are suppressed because of a low crew count.
    /// </summary>
    private bool _autoDisabled;

    private void InitializeAutoToggle()
    {
        _autoDisableEnabled = _cfg.GetCVar(CCCVars.SkillsAutoDisableEnabled);
        _autoDisableThreshold = _cfg.GetCVar(CCCVars.SkillsAutoDisableThreshold);
        _autoDisableHysteresis = _cfg.GetCVar(CCCVars.SkillsAutoDisableHysteresis);

        Subs.CVar(_cfg, CCCVars.SkillsAutoDisableEnabled, value =>
        {
            _autoDisableEnabled = value;

            // Turning the feature off must not leave skills suppressed forever.
            if (!value)
                _autoDisabled = false;
        });
        Subs.CVar(_cfg, CCCVars.SkillsAutoDisableThreshold, value => _autoDisableThreshold = value);
        Subs.CVar(_cfg, CCCVars.SkillsAutoDisableHysteresis, value => _autoDisableHysteresis = value);

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        _autoDisabled = false;
        _nextCheck = TimeSpan.Zero;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.CurTime < _nextCheck)
            return;

        _nextCheck = _timing.CurTime + CheckInterval;

        UpdateAutoToggle();
    }

    private void UpdateAutoToggle()
    {
        if (!_autoDisableEnabled || !_skillsEnabled)
            return;

        // Only makes sense mid-round, the manifest is empty otherwise.
        if (_ticker.RunLevel != GameRunLevel.InRound)
            return;

        var crew = GetStationCrewCount();

        if (!_autoDisabled)
        {
            if (crew >= _autoDisableThreshold)
                return;

            _autoDisabled = true;
            Announce("skills-auto-disabled-announcement", crew);
            Log.Info($"Skills auto-disabled: {crew} crew members on station (threshold {_autoDisableThreshold}).");
            return;
        }

        // Require a bit more than the threshold to switch back, so the state doesn't flicker.
        if (crew < _autoDisableThreshold + _autoDisableHysteresis)
            return;

        _autoDisabled = false;
        Announce("skills-auto-enabled-announcement", crew);
        Log.Info($"Skills auto-enabled: {crew} crew members on station (threshold {_autoDisableThreshold}).");
    }

    private void Announce(string locId, int crew)
    {
        _chatManager.DispatchServerAnnouncement(
            Loc.GetString(locId, ("count", crew), ("threshold", _autoDisableThreshold)));
    }

    /// <summary>
    /// Counts connected players whose character is still on the crew manifest.
    /// Dead and crit crew members still count - their body is on the station and can be revived.
    /// Not counted: players in the lobby, players without a job (ghost roles, antag spawns, etc.)
    /// and everyone who left through cryosleep, since cryo wipes the manifest record.
    /// </summary>
    public int GetStationCrewCount()
    {
        var count = 0;

        foreach (var session in _player.Sessions)
        {
            if (session.Status != SessionStatus.InGame)
                continue;

            // Sitting in the lobby doesn't count as being on the station.
            if (session.AttachedEntity is null)
                continue;

            if (!_mind.TryGetMind(session.UserId, out var mind))
                continue;

            if (!_roles.MindHasRole<JobRoleComponent>(mind.Value.Owner))
                continue;

            if (!TryGetManifestBody(mind.Value, out var body))
                continue;

            // Cryo removes the station record, so the character is off the manifest.
            if (HasComp<CryostorageContainedComponent>(body))
                continue;

            count++;
        }

        return count;
    }

    /// <summary>
    /// Resolves the body the mind is signed on the manifest with. While ghosting the mind owns the
    /// ghost entity, so the original body is used instead - that's what keeps dead players counted.
    /// </summary>
    private bool TryGetManifestBody(Entity<MindComponent> mind, out EntityUid body)
    {
        body = default;

        var owned = mind.Comp.OwnedEntity;

        if (owned is null || HasComp<GhostComponent>(owned))
            owned = GetEntity(mind.Comp.OriginalOwnedEntity);

        // No body left at all - gibbed, deleted or never spawned.
        if (owned is not { } uid || TerminatingOrDeleted(uid) || HasComp<GhostComponent>(uid))
            return false;

        body = uid;
        return true;
    }
}
