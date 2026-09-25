// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.Database;
using Content.Server.Discord;
using Content.Shared.CCVar;
using Content.Shared.Database;
using Content.Shared.Roles;

namespace Content.Server.Administration.Managers;

public sealed partial class BanManager
{
    // МирДи: все новые баны (серверные и ролевые) дублируются в Discord через вебхук discord.ban_webhook.

    [Dependency] private readonly DiscordWebhook _discord = default!;

    private const int BanWebhookServerColor = 0xE74C3C;
    private const int BanWebhookRoleColor = 0xE67E22;

    // Лимит Discord на значение поля embed.
    private const int BanWebhookFieldLimit = 1024;

    private string _banWebhookUrl = string.Empty;
    private WebhookIdentifier? _banWebhookId;

    private void InitializeBanWebhook()
    {
        _cfg.OnValueChanged(CCVars.DiscordBanWebhook, OnBanWebhookChanged, true);
    }

    private void OnBanWebhookChanged(string url)
    {
        _banWebhookUrl = url;
        _banWebhookId = null;
    }

    private async void SendBanWebhook(BanDef ban, string targetName)
    {
        if (_banWebhookUrl == string.Empty)
            return;

        try
        {
            // Идентификатор получаем лениво: если Discord был недоступен при старте, следующий бан попробует снова.
            if (_banWebhookId == null)
            {
                if (await _discord.GetWebhook(_banWebhookUrl) is not { } data)
                    return;

                _banWebhookId = data.ToIdentifier();
            }

            var adminName = ban.BanningAdmin == null
                ? Loc.GetString("system-user")
                : (await _db.GetPlayerRecordByUserId(ban.BanningAdmin.Value))?.LastSeenUserName ?? Loc.GetString("system-user");

            var target = targetName == "null" ? "не указан (бан по IP/HWID)" : targetName;

            var length = ban.ExpirationTime is { } expires
                ? $"до <t:{expires.ToUnixTimeSeconds()}:f> (<t:{expires.ToUnixTimeSeconds()}:R>)"
                : "навсегда";

            var fields = new List<WebhookEmbedField>
            {
                new() { Name = "Игрок", Value = Truncate(target), Inline = false },
                new() { Name = "Администратор", Value = Truncate(adminName) },
                new() { Name = "Срок", Value = length },
            };

            if (ban.Roles is { } roles)
            {
                var roleNames = string.Join(", ", roles.Select(GetBanRoleName));
                fields.Add(new() { Name = "Роли", Value = Truncate(roleNames), Inline = false });
            }

            var reason = string.IsNullOrWhiteSpace(ban.Reason) ? "—" : ban.Reason;
            fields.Add(new() { Name = "Причина", Value = Truncate(reason), Inline = false });

            var footer = $"Тяжесть: {ban.Severity}";
            if (ban.RoundIds.Length > 0)
                footer = $"Раунд {string.Join(", ", ban.RoundIds)} · {footer}";

            var embed = new WebhookEmbed
            {
                Title = ban.Roles == null ? "Бан на сервере" : "Бан роли",
                Color = ban.Roles == null ? BanWebhookServerColor : BanWebhookRoleColor,
                Fields = fields,
                Footer = new WebhookEmbedFooter { Text = footer },
            };

            var response = await _discord.CreateMessage(_banWebhookId.Value, new WebhookPayload { Embeds = [embed] });
            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                _sawmill.Error($"Discord ban webhook returned {(int) response.StatusCode}: {content}");
            }
        }
        catch (Exception e)
        {
            _sawmill.Error($"Error while sending ban to Discord webhook: {e}");
        }
    }

    private string GetBanRoleName(BanRoleDef role)
    {
        if (role.RoleType == DbTypeJob && _prototypeManager.TryIndex<JobPrototype>(role.RoleId, out var job))
            return job.LocalizedName;

        if (role.RoleType == DbTypeAntag && _prototypeManager.TryIndex<AntagPrototype>(role.RoleId, out var antag))
            return Loc.GetString(antag.Name);

        return role.ToString();
    }

    private static string Truncate(string value)
    {
        return value.Length <= BanWebhookFieldLimit ? value : value[..(BanWebhookFieldLimit - 1)] + "…";
    }
}
