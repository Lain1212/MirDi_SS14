// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.GameTicking;
using Content.Server.Maps;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Robust.Server.Player;
using Robust.Shared.Configuration;

namespace Content.Server.Discord.DiscordLink;

/// <summary>
///     МирДи: показывает в статусе Discord-бота онлайн, время раунда и карту.
/// </summary>
public sealed class DiscordStatusSystem : EntitySystem
{
    [Dependency] private readonly DiscordLink _discordLink = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IGameMapManager _gameMapManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;

    // Статус шлётся только при изменении текста, так что частая проверка не упирается в лимиты Discord.
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(30);

    private TimeSpan _accumulator;
    private string? _lastStatus;

    public override void Initialize()
    {
        base.Initialize();

        // После переподключения к Discord статус сбрасывается — отправляем заново.
        _discordLink.OnReady += () => _lastStatus = null;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _accumulator += TimeSpan.FromSeconds(frameTime);
        if (_accumulator < UpdateInterval)
            return;

        _accumulator = TimeSpan.Zero;

        var status = BuildStatus();
        if (status == _lastStatus)
            return;

        _lastStatus = status;
        _discordLink.UpdateStatus(status);
    }

    private string BuildStatus()
    {
        var players = $"👥 {_playerManager.PlayerCount} из {_cfg.GetCVar(CCVars.SoftMaxPlayers)}";

        var time = _gameTicker.RunLevel switch
        {
            GameRunLevel.PreRoundLobby => "🕐 лобби",
            GameRunLevel.PostRound => "🕐 конец раунда",
            _ => $"🕐 {FormatDuration(_gameTicker.RoundDuration())}",
        };

        var status = $"{players} {time}";
        if (_gameMapManager.GetSelectedMap()?.MapName is { } map)
            status += $" 🗺 {map}";

        return status;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? $"{(int) duration.TotalHours}ч {duration.Minutes}м"
            : $"{duration.Minutes}м";
    }
}
