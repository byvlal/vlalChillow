using System.Globalization;
using System.Text;
using StandChillow.Bot.Contracts;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace StandChillow.Bot;

/// <summary>Экраны профиля, матчей, игрока, жалоб и администрации.</summary>
public sealed partial class TelegramBot
{
    // ── диспетчер кнопок ────────────────────────────────────────────────────

    private async Task HandleCallbackAsync(
        ChatTarget chat, CallbackQuery query, CancellationToken ct)
    {
        try
        {
            await _bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);
        }
        catch
        {
            // Просроченный callback — не повод ронять обработку.
        }

        var data = query.Data ?? string.Empty;
        var messageId = query.Message?.Id;
        var parts = data.Split(':');

        try
        {
            switch (parts[0])
            {
                case "menu":
                    await ShowMenuAsync(chat, messageId, ct);
                    return;

                case "status":
                    await ShowStatusAsync(chat, messageId, ct);
                    return;

                case "rooms":
                    await ShowRoomsAsync(chat, messageId, IntPart(parts, 1, 1), ct);
                    return;

                case "room":
                {
                    var number = IntPart(parts, 1);
                    if (number <= 0)
                        await ShowMenuAsync(chat, messageId, ct);
                    else
                        await ShowRoomAsync(chat, messageId, number, ct);
                    return;
                }

                case "roomend":
                {
                    var number = IntPart(parts, 1);
                    if (number <= 0)
                    {
                        await ShowMenuAsync(chat, messageId, ct);
                        return;
                    }

                    if (parts.Length >= 3 && parts[2] == "yes")
                        await EndRoomAsync(chat, messageId, number, ct);
                    else
                        await ShowEndConfirmAsync(chat, messageId, number, ct);
                    return;
                }

                case "create":
                    await HandleCreateCallbackAsync(chat, messageId, parts, ct);
                    return;

                case "profile":
                    await ShowProfileAsync(chat, messageId, ct);
                    return;

                case "stats":
                    await ShowStatsAsync(chat, messageId, ct);
                    return;

                case "matches":
                    await ShowMatchesAsync(
                        chat, messageId,
                        IntPart(parts, 1), IntPart(parts, 2, 1), ct);
                    return;

                case "match":
                {
                    var id = IntPart(parts, 1);
                    if (id <= 0)
                        await ShowMenuAsync(chat, messageId, ct);
                    else
                        await ShowMatchAsync(chat, messageId, id, IntPart(parts, 2), ct);
                    return;
                }

                case "player":
                    if (parts.Length >= 2)
                        await LookupPlayerAsync(chat, messageId, parts[1], ct);
                    else
                        await ShowPlayerPromptAsync(chat, messageId, ct);
                    return;

                case "report":
                    await HandleReportCallbackAsync(chat, messageId, parts, ct);
                    return;

                case "reports":
                    await ShowMyReportsAsync(chat, messageId, IntPart(parts, 1, 1), ct);
                    return;

                case "admins":
                    await ShowAdministrationAsync(chat, messageId, ct);
                    return;

                case "help":
                    await ShowHelpAsync(chat, messageId, ct);
                    return;

                case "logout":
                    if (parts.Length >= 2 && parts[1] == "yes")
                        await LogoutAsync(chat, messageId, ct);
                    else
                        await ShowLogoutConfirmAsync(chat, messageId, ct);
                    return;

                default:
                    await EditOrSendAsync(
                        chat, messageId,
                        "Кнопка устарела. Открой меню заново — /menu",
                        MenuKeyboard(), ct);
                    return;
            }
        }
        catch (PanelApiException ex)
        {
            await HandlePanelErrorAsync(chat, messageId, ex, null, ct);
        }
        catch (Exception ex)
        {
            Log($"callback {data}: {ex}");
            await EditOrSendAsync(
                chat, messageId,
                "Что-то пошло не так. Попробуй ещё раз или открой /menu.",
                MenuKeyboard(), ct);
        }
    }

    private async Task HandleCreateCallbackAsync(
        ChatTarget chat, int? messageId, string[] parts, CancellationToken ct)
    {
        if (parts.Length < 2)
        {
            await ShowCreateModesAsync(chat, messageId, ct);
            return;
        }

        var state = State(chat.UserId);
        var wizard = state.Wizard as CreateRoomWizard;

        switch (parts[1])
        {
            case "mode":
                if (parts.Length >= 3)
                    await ShowCreateMapsAsync(chat, messageId, parts[2], ct);
                else
                    await ShowCreateModesAsync(chat, messageId, ct);
                return;

            case "map":
            {
                if (wizard?.Mode is null)
                {
                    await ShowCreateModesAsync(chat, messageId, ct);
                    return;
                }

                var index = IntPart(parts, 2, -1);
                await ShowCreateTeamsAsync(
                    chat, messageId, wizard.Mode.Id, index, ct);
                return;
            }

            case "team":
            {
                if (wizard?.Mode is null || wizard.Level is null)
                {
                    await ShowCreateModesAsync(chat, messageId, ct);
                    return;
                }

                var size = IntPart(parts, 2);
                if (size <= 0 || !wizard.Mode.TeamSizes.Contains(size))
                {
                    await ShowCreateMapsAsync(chat, messageId, wizard.Mode.Id, ct);
                    return;
                }

                wizard.PerTeam = size;
                await ShowCreateNameAsync(chat, messageId, ct);
                return;
            }

            case "noname":
                if (wizard is null)
                {
                    await ShowCreateModesAsync(chat, messageId, ct);
                    return;
                }

                await CreateRoomAsync(chat, messageId, wizard, string.Empty, ct);
                return;

            default:
                await ShowCreateModesAsync(chat, messageId, ct);
                return;
        }
    }

    private async Task HandleReportCallbackAsync(
        ChatTarget chat, int? messageId, string[] parts, CancellationToken ct)
    {
        var targetId = IntPart(parts, 1);
        if (targetId <= 0)
        {
            await ShowMenuAsync(chat, messageId, ct);
            return;
        }

        if (parts.Length == 2)
        {
            await ShowReportCategoriesAsync(chat, messageId, targetId, ct);
            return;
        }

        var category = parts[2];
        if (parts.Length >= 4 && parts[3] == "skip")
        {
            var wizard = State(chat.UserId).Wizard as ReportWizard;
            if (wizard is null || wizard.TargetId != targetId)
            {
                await ShowReportCategoriesAsync(chat, messageId, targetId, ct);
                return;
            }

            wizard.Category = category;
            await SubmitReportAsync(chat, messageId, wizard, string.Empty, ct);
            return;
        }

        await ShowReportReasonAsync(chat, messageId, targetId, category, ct);
    }

    private static int IntPart(string[] parts, int index, int fallback = 0)
    {
        if (parts.Length <= index)
            return fallback;
        return int.TryParse(
            parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    // ── профиль и статистика ────────────────────────────────────────────────

    private async Task ShowProfileAsync(ChatTarget chat, int? messageId, CancellationToken ct)
    {
        var binding = await RequireBindingAsync(chat, messageId, ct);
        if (binding is null)
            return;

        try
        {
            var profile = await _panel.WhoAmIAsync(binding.SessionToken, ct);
            var text = Fmt.JoinLines(
                $"👤 <b>{Fmt.E(profile.Nick)}</b>",
                $"ID: {Fmt.E(profile.DisplayId)}",
                $"Сервер: {Fmt.E(_config.Label)}",
                profile.Admin ? "👑 Администратор" : null);

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    Btn("📊 Статистика", "stats"),
                    Btn("🏆 Матчи", "matches:0:1"),
                },
                new[]
                {
                    Btn("🚪 Выйти", "logout"),
                    Btn("🏠 Меню", "menu"),
                },
            });

            await EditOrSendAsync(chat, messageId, text, keyboard, ct);
        }
        catch (PanelApiException ex)
        {
            await HandlePanelErrorAsync(chat, messageId, ex, "profile", ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chat, messageId, ex, "profile", ct);
        }
    }

    private async Task ShowStatsAsync(ChatTarget chat, int? messageId, CancellationToken ct)
    {
        var binding = await RequireBindingAsync(chat, messageId, ct);
        if (binding is null)
            return;

        try
        {
            var stats = await _panel.GetMyStatsAsync(binding.SessionToken, ct);
            var text = Fmt.JoinLines(
                "📊 <b>Статистика</b>",
                $"Убийств: {stats.Kills}",
                $"Смертей: {stats.Deaths}",
                $"K/D: {Fmt.Kd(stats.Kd)}",
                $"В игре: {Fmt.Duration(stats.Seconds)}",
                $"Матчей: {stats.Matches}");

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    Btn("🔄 Обновить", "stats"),
                    Btn("🏆 Матчи", "matches:0:1"),
                },
                new[] { Btn("🏠 Меню", "menu") },
            });

            await EditOrSendAsync(chat, messageId, text, keyboard, ct);
        }
        catch (PanelApiException ex)
        {
            await HandlePanelErrorAsync(chat, messageId, ex, "stats", ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chat, messageId, ex, "stats", ct);
        }
    }

    // ── матчи ───────────────────────────────────────────────────────────────

    private async Task ShowMatchesAsync(
        ChatTarget chat, int? messageId, int playerId, int page, CancellationToken ct)
    {
        var binding = await RequireBindingAsync(chat, messageId, ct);
        if (binding is null)
            return;

        var selfId = binding.PlayerId;
        var targetId = playerId > 0 ? playerId : selfId;

        try
        {
            var response = await _panel.GetMatchesAsync(targetId, 20, ct);
            var matches = response.Matches;

            const int pageSize = 5;
            var pages = Math.Max(1, (matches.Count + pageSize - 1) / pageSize);
            page = Math.Clamp(page, 1, pages);

            var title = targetId == selfId
                ? "🏆 <b>Мои матчи</b>"
                : $"🏆 <b>Матчи игрока</b> {targetId}";

            var text = new StringBuilder();
            text.AppendLine(title);
            if (matches.Count == 0)
            {
                text.AppendLine();
                text.AppendLine("Матчей пока нет.");
            }
            else
            {
                text.AppendLine($"Всего: {matches.Count} · страница {page}/{pages}");
            }

            var rows = new List<InlineKeyboardButton[]>();
            foreach (var match in matches.Skip((page - 1) * pageSize).Take(pageSize))
            {
                var label =
                    $"#{match.Id} · {Fmt.DateShort(match.Ended, _config.TimeZone)} · " +
                    $"{match.ScoreCt}:{match.ScoreTr}";
                rows.Add(new[]
                {
                    Btn(label, $"match:{match.Id}:{targetId}"),
                });
            }

            if (pages > 1)
            {
                var nav = new List<InlineKeyboardButton>();
                if (page > 1)
                    nav.Add(Btn("⬅️", $"matches:{targetId}:{page - 1}"));
                nav.Add(Btn($"{page}/{pages}", $"matches:{targetId}:{page}"));
                if (page < pages)
                    nav.Add(Btn("➡️", $"matches:{targetId}:{page + 1}"));
                rows.Add(nav.ToArray());
            }

            rows.Add(new[]
            {
                Btn("⬅️ Назад", targetId == selfId ? "profile" : $"player:{targetId}"),
                Btn("🏠 Меню", "menu"),
            });

            await EditOrSendAsync(
                chat, messageId, text.ToString().TrimEnd(),
                new InlineKeyboardMarkup(rows), ct);
        }
        catch (PanelApiException ex)
        {
            await HandlePanelErrorAsync(chat, messageId, ex, $"matches:{targetId}:{page}", ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chat, messageId, ex, $"matches:{targetId}:{page}", ct);
        }
    }

    private async Task ShowMatchAsync(
        ChatTarget chat, int? messageId, int id, int playerId, CancellationToken ct)
    {
        var binding = ActiveBinding(chat.UserId);
        try
        {
            var match = await _panel.GetMatchAsync(id, ct);

            var text = new StringBuilder();
            text.AppendLine($"🏆 <b>Матч #{match.Id}</b>");
            text.AppendLine($"{Fmt.E(match.Mode)} · {Fmt.E(match.Map)}");
            text.AppendLine(
                $"Счёт: CT {match.ScoreCt} : {match.ScoreTr} T · " +
                $"победитель: {Fmt.Winner(match.Winner)}");
            text.AppendLine(
                $"Раундов: {match.Rounds} · {Fmt.Date(match.Ended, _config.TimeZone)}");
            if (!string.IsNullOrWhiteSpace(match.RoomName))
                text.AppendLine($"Комната #{match.Room} «{Fmt.E(match.RoomName)}»");

            if (match.Players.Count > 0)
            {
                text.AppendLine();
                foreach (var group in match.Players
                             .GroupBy(player => player.Team)
                             .OrderBy(group => TeamOrder(group.Key)))
                {
                    text.AppendLine($"<b>{Fmt.Team(group.Key)}</b>");
                    foreach (var player in group.OrderByDescending(p => p.Kills))
                    {
                        text.Append("• ");
                        text.Append(Fmt.E(player.Nick));
                        text.Append(
                            $" — {player.Kills}/{player.Assists}/{player.Deaths}");
                        if (binding is not null && player.Id == binding.PlayerId)
                            text.Append(" ← ты");
                        if (player.Left)
                            text.Append(" (вышел)");
                        text.AppendLine();
                    }
                }
            }

            var backPlayer = playerId > 0 ? playerId : binding?.PlayerId ?? 0;
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    Btn("⬅️ Матчи", $"matches:{backPlayer}:1"),
                    Btn("🏠 Меню", "menu"),
                },
            });

            await EditOrSendAsync(
                chat, messageId, text.ToString().TrimEnd(), keyboard, ct);
        }
        catch (PanelApiException ex)
        {
            await HandlePanelErrorAsync(chat, messageId, ex, "matches:0:1", ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chat, messageId, ex, "matches:0:1", ct);
        }
    }

    /// <summary>
    /// В группе обычный текст бот не читает: чтобы ответ на вопрос попал в визард,
    /// его нужно прислать ответом на сообщение бота или с упоминанием.
    /// </summary>
    private string ReplyHint(ChatTarget chat)
    {
        if (!chat.IsGroup)
            return string.Empty;
        return _botUsername is null
            ? "\n\n↩️ В группе ответь на это сообщение."
            : $"\n\n↩️ В группе ответь на это сообщение или упомяни @{_botUsername}.";
    }

    // ── игрок ───────────────────────────────────────────────────────────────

    private async Task ShowPlayerPromptAsync(
        ChatTarget chat, int? messageId, CancellationToken ct)
    {
        State(chat.UserId).Wizard = new PlayerLookupWizard();

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { Btn("⬅️ Отмена", "menu") },
        });

        await EditOrSendAsync(
            chat, messageId,
            "🔍 <b>Поиск игрока</b>\n\n" +
            "Пришли ID, кастомный ID или ник — бот покажет профиль и статистику." +
            ReplyHint(chat),
            keyboard, ct);
    }

    private async Task LookupPlayerAsync(
        ChatTarget chat, int? messageId, string query, CancellationToken ct)
    {
        State(chat.UserId).Wizard = null;
        var text = query.Trim();
        if (text.Length == 0)
        {
            await ShowPlayerPromptAsync(chat, messageId, ct);
            return;
        }

        try
        {
            var player = await _panel.GetPlayerAsync(text, ct);
            var body = Fmt.JoinLines(
                $"👤 <b>{Fmt.E(player.Nick)}</b>",
                $"ID: {Fmt.E(player.DisplayId)}",
                $"Статус: {Fmt.PlayerStatus(player.Status)}",
                player.Admin ? "👑 Администратор" : null,
                player.Banned ? "⛔ Забанен" : null,
                "",
                $"K/D: {Fmt.Kd(player.Stats.Kd)} · " +
                $"{player.Stats.Kills}/{player.Stats.Deaths}",
                $"Матчей: {player.Stats.Matches}",
                $"Жалоб: {player.Reports.Total} " +
                $"(открыто {player.Reports.Open}, принято {player.Reports.Accepted})",
                string.IsNullOrWhiteSpace(player.Note)
                    ? null
                    : $"Заметка: {Fmt.E(player.Note)}");

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    Btn("🚨 Пожаловаться", $"report:{player.Id}"),
                    Btn("🏆 Матчи", $"matches:{player.Id}:1"),
                },
                new[]
                {
                    Btn("🔍 Ещё", "player"),
                    Btn("🏠 Меню", "menu"),
                },
            });

            await EditOrSendAsync(chat, messageId, body, keyboard, ct);
        }
        catch (PanelApiException ex) when (ex.Code == "not_found")
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    Btn("🔍 Ещё", "player"),
                    Btn("🏠 Меню", "menu"),
                },
            });
            await EditOrSendAsync(
                chat, messageId,
                "Никого не нашёл. Проверь ID или ник и попробуй ещё раз.",
                keyboard, ct);
        }
        catch (PanelApiException ex)
        {
            await HandlePanelErrorAsync(chat, messageId, ex, "player", ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chat, messageId, ex, "player", ct);
        }
    }

    // ── жалобы ──────────────────────────────────────────────────────────────

    private async Task ShowReportCategoriesAsync(
        ChatTarget chat, int? messageId, int targetId, CancellationToken ct)
    {
        State(chat.UserId).Wizard = new ReportWizard
        {
            TargetId = targetId,
            Step = "category",
        };

        var rows = new List<InlineKeyboardButton[]>
        {
            new[]
            {
                Btn("Читы", $"report:{targetId}:cheat"),
                Btn("Тимкилл", $"report:{targetId}:teamkill"),
            },
            new[]
            {
                Btn("Оскорбления", $"report:{targetId}:insult"),
                Btn("Спам", $"report:{targetId}:spam"),
            },
            new[]
            {
                Btn("Другое", $"report:{targetId}:other"),
            },
            new[]
            {
                Btn("⬅️ Назад", $"player:{targetId}"),
                Btn("🏠 Меню", "menu"),
            },
        };

        await EditOrSendAsync(
            chat, messageId,
            $"🚨 <b>Жалоба на игрока {targetId}</b>\n\nВыбери причину.",
            new InlineKeyboardMarkup(rows), ct);
    }

    private async Task ShowReportReasonAsync(
        ChatTarget chat, int? messageId, int targetId, string category,
        CancellationToken ct)
    {
        if (!IsReportCategory(category))
        {
            await ShowReportCategoriesAsync(chat, messageId, targetId, ct);
            return;
        }

        var state = State(chat.UserId);
        var wizard = state.Wizard as ReportWizard;
        if (wizard is null || wizard.TargetId != targetId)
        {
            wizard = new ReportWizard { TargetId = targetId };
            state.Wizard = wizard;
        }

        wizard.Category = category;
        wizard.Step = "reason";

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { Btn("Без текста", $"report:{targetId}:{category}:skip") },
            new[] { Btn("⬅️ Отмена", "menu") },
        });

        await EditOrSendAsync(
            chat, messageId,
            $"🚨 <b>Жалоба: {CategoryLabel(category)}</b>\n\n" +
            "Опиши причину (до 500 символов) или нажми «Без текста»." +
            ReplyHint(chat),
            keyboard, ct);
    }

    private async Task SubmitReportAsync(
        ChatTarget chat, int? messageId, ReportWizard wizard, string reason,
        CancellationToken ct)
    {
        var binding = await RequireBindingAsync(chat, messageId, ct);
        if (binding is null)
            return;

        if (string.IsNullOrWhiteSpace(wizard.Category)
            || !IsReportCategory(wizard.Category))
        {
            await ShowReportCategoriesAsync(chat, messageId, wizard.TargetId, ct);
            return;
        }

        reason = reason.Trim();
        if (reason.Length > 500)
            reason = reason[..500];

        try
        {
            var result = await _panel.SubmitReportAsync(
                binding.SessionToken, wizard.TargetId, wizard.Category, reason, ct);

            State(chat.UserId).Wizard = null;

            var text = Fmt.JoinLines(
                "✅ <b>Жалоба отправлена</b>",
                $"На игрока: {Fmt.E(result.Report.TargetNick)}",
                $"Причина: {Fmt.E(result.Report.CategoryLabel)}",
                $"Статус: {Fmt.E(result.Report.StatusLabel)}",
                "",
                "Модерация увидит её в очереди.");

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    Btn("🚨 Мои жалобы", "reports:1"),
                    Btn("🏠 Меню", "menu"),
                },
            });

            await EditOrSendAsync(chat, messageId, text, keyboard, ct);
        }
        catch (PanelApiException ex)
        {
            var text = ex.Code switch
            {
                "already_reported" =>
                    "Ты уже жаловался на этого игрока за последние 24 часа.",
                "daily_limit" => "На сегодня лимит жалоб исчерпан.",
                "cooldown" =>
                    ex.RetryAfterSec > 0
                        ? $"Подожди {ex.RetryAfterSec} с перед следующей жалобой."
                        : "Подожди немного перед следующей жалобой.",
                "self_report" => "На себя жаловаться нельзя.",
                "not_found" => "Игрок не найден — возможно, уже вышел и след пропал.",
                "reason_too_long" => "Причина слишком длинная (максимум 500 символов).",
                _ => $"Не получилось отправить жалобу: {Fmt.E(ex.Message)}",
            };

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { Btn("🚨 Мои жалобы", "reports:1") },
                new[] { Btn("🏠 Меню", "menu") },
            });

            await EditOrSendAsync(chat, messageId, text, keyboard, ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chat, messageId, ex, "reports:1", ct);
        }
    }

    private async Task ShowMyReportsAsync(
        ChatTarget chat, int? messageId, int page, CancellationToken ct)
    {
        var binding = await RequireBindingAsync(chat, messageId, ct);
        if (binding is null)
            return;

        try
        {
            var response = await _panel.GetMyReportsAsync(
                binding.SessionToken, Math.Max(1, page), ct);

            const int pageSize = 5;
            var pages = Math.Max(1, (response.Items.Count + pageSize - 1) / pageSize);
            page = Math.Clamp(page, 1, pages);

            var text = new StringBuilder();
            text.AppendLine($"🚨 <b>Мои жалобы</b> · {response.Total}");
            if (response.Items.Count == 0)
            {
                text.AppendLine();
                text.AppendLine("Ты ещё ни на кого не жаловался.");
            }
            else
            {
                text.AppendLine($"Страница {page}/{pages}");
                foreach (var item in response.Items.Skip((page - 1) * pageSize).Take(pageSize))
                {
                    text.AppendLine();
                    text.AppendLine(
                        $"• <b>{Fmt.E(item.TargetNick)}</b> · " +
                        $"{Fmt.E(item.CategoryLabel)} · {Fmt.E(item.StatusLabel)}");
                    text.AppendLine($"  {Fmt.Date(item.Created, _config.TimeZone)}");
                    if (!string.IsNullOrWhiteSpace(item.Reason))
                        text.AppendLine($"  «{Fmt.E(Fmt.Short(item.Reason, 80))}»");
                }
            }

            var rows = new List<InlineKeyboardButton[]>();
            if (pages > 1)
            {
                var nav = new List<InlineKeyboardButton>();
                if (page > 1)
                    nav.Add(Btn("⬅️", $"reports:{page - 1}"));
                nav.Add(Btn($"{page}/{pages}", $"reports:{page}"));
                if (page < pages)
                    nav.Add(Btn("➡️", $"reports:{page + 1}"));
                rows.Add(nav.ToArray());
            }

            rows.Add(new[]
            {
                Btn("🔄 Обновить", $"reports:{page}"),
                Btn("🏠 Меню", "menu"),
            });

            await EditOrSendAsync(
                chat, messageId, text.ToString().TrimEnd(),
                new InlineKeyboardMarkup(rows), ct);
        }
        catch (PanelApiException ex)
        {
            await HandlePanelErrorAsync(chat, messageId, ex, $"reports:{page}", ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chat, messageId, ex, $"reports:{page}", ct);
        }
    }

    private static bool IsReportCategory(string category) =>
        category is "cheat" or "teamkill" or "insult" or "spam" or "other";

    private static string CategoryLabel(string category) => category switch
    {
        "cheat" => "читерство",
        "teamkill" => "тимкилл",
        "insult" => "оскорбления",
        "spam" => "спам",
        "other" => "другое",
        _ => category,
    };

    // ── администрация ───────────────────────────────────────────────────────

    private async Task ShowAdministrationAsync(
        ChatTarget chat, int? messageId, CancellationToken ct)
    {
        try
        {
            var response = await _panel.GetAdministrationAsync(ct);
            var text = new StringBuilder();
            text.AppendLine($"👑 <b>Администрация</b> · {response.Admins.Count}");
            text.AppendLine($"Сервер: {Fmt.E(_config.Label)}");

            if (response.Admins.Count == 0)
            {
                text.AppendLine();
                text.AppendLine("Сервер не отдал ни одного админа.");
            }
            else
            {
                foreach (var admin in response.Admins)
                {
                    var nick = string.IsNullOrWhiteSpace(admin.Nick)
                        ? admin.DisplayId
                        : admin.Nick;
                    text.AppendLine();
                    text.Append($"• <b>{Fmt.E(nick)}</b>");
                    text.Append($" — {Fmt.PlayerStatus(admin.Status)}");
                    if (admin.Reports.Open > 0)
                        text.Append($" · жалоб: {admin.Reports.Open}");
                    text.AppendLine();
                }
            }

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    Btn("🔄 Обновить", "admins"),
                    Btn("🏠 Меню", "menu"),
                },
            });

            await EditOrSendAsync(
                chat, messageId, text.ToString().TrimEnd(), keyboard, ct);
        }
        catch (PanelApiException ex)
        {
            await HandlePanelErrorAsync(chat, messageId, ex, "admins", ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chat, messageId, ex, "admins", ct);
        }
    }

    // ── инструкция и выход ──────────────────────────────────────────────────

    private async Task ShowHelpAsync(ChatTarget chat, int? messageId, CancellationToken ct)
    {
        var text =
            "📖 <b>Как подключиться</b>\n\n" +
            "1. Запусти игру.\n" +
            $"2. Адрес сервера: <code>{Fmt.E(_config.GameAddress)}</code>\n" +
            $"   ({Fmt.E(_config.Label)})\n" +
            "3. Зайди в лобби и возьми код из 6 цифр.\n" +
            "4. Пришли код сюда — бот запомнит тебя.\n\n" +
            "Комнаты создаются кнопкой «Создать». Список комнат, статус и матчи — в меню.\n\n" +
            "Код живёт 30 минут. Если сессия истекла, бот попросит новый код.";

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                Btn("🟢 Статус", "status"),
                Btn("🏠 Меню", "menu"),
            },
        });

        await EditOrSendAsync(chat, messageId, text, keyboard, ct);
    }

    private async Task ShowLogoutConfirmAsync(
        ChatTarget chat, int? messageId, CancellationToken ct)
    {
        var binding = ActiveBinding(chat.UserId);
        if (binding is null)
        {
            await ShowAuthPromptAsync(chat, messageId, ct);
            return;
        }

        var text =
            $"Выйти из аккаунта <b>{Fmt.E(binding.Nick)}</b>?\n\n" +
            "Придётся заново ввести код из лобби.";

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { Btn("Да, выйти", "logout:yes") },
            new[] { Btn("Нет", "menu") },
        });

        await EditOrSendAsync(chat, messageId, text, keyboard, ct);
    }

    private async Task LogoutAsync(ChatTarget chat, int? messageId, CancellationToken ct)
    {
        var binding = ActiveBinding(chat.UserId);
        if (binding is null)
        {
            await ShowAuthPromptAsync(chat, messageId, ct);
            return;
        }

        try
        {
            await _panel.LogoutAsync(binding.SessionToken, ct);
        }
        catch (Exception ex)
        {
            Log($"logout: {ex.Message}");
        }

        _store.Delete(chat.UserId);
        State(chat.UserId).Wizard = null;

        await ShowAuthPromptAsync(
            chat, messageId, ct,
            "Вышел из аккаунта. Чтобы войти снова, пришли новый код из лобби.");
    }
}
