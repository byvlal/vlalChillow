using System.Collections.Concurrent;
using System.Net;
using System.Text;
using StandChillow.Bot.Contracts;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace StandChillow.Bot;

/// <summary>
/// Кнопочный Telegram-бот влалChillow: вход по коду из лобби, комнаты, профиль,
/// матчи, жалобы и администрация — без админки сайта.
/// </summary>
/// <remarks>
/// <para>
/// Один процесс обслуживает всех: сессия игрока не лежит в поле класса, а приходит
/// из SQLite по Telegram ID. Поэтому два человека могут одновременно сидеть в боте
/// и не видеть чужие данные.
/// </para>
/// <para>
/// Состояние визардов (создание комнаты, жалоба, поиск) — в памяти: после перезапуска
/// бот просто попросит начать заново, а привязка и сессия останутся.
/// </para>
/// </remarks>
public sealed partial class TelegramBot
{
    private readonly BotConfig _config;
    private readonly BotStore _store;
    private readonly PanelClient _panel;
    private readonly ITelegramBotClient _bot;
    private readonly OutgoingRateLimiter _limiter = new();
    private readonly ConcurrentDictionary<long, UserState> _states = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();
    private string? _botUsername;
    private long _botId;

    public TelegramBot(
        BotConfig config, BotStore store, PanelClient panel)
    {
        _config = config;
        _store = store;
        _panel = panel;

        HttpClient? http = null;
        if (!string.IsNullOrWhiteSpace(config.TelegramProxy))
        {
            http = new HttpClient(new SocketsHttpHandler
            {
                Proxy = new WebProxy(config.TelegramProxy),
                UseProxy = true,
            });
        }

        _bot = new TelegramBotClient(config.TelegramToken, http);
    }

    // ── запуск ──────────────────────────────────────────────────────────────

    public async Task RunAsync(CancellationToken ct)
    {
        Log($"сервер ({_config.Label}): панель {_config.ApiBaseUrl}, " +
            $"игра {_config.GameAddress}");
        Log($"база: {_config.DbPath}");

        try
        {
            var me = await _bot.GetMe(cancellationToken: ct);
            _botUsername = me.Username;
            _botId = me.Id;
            Log($"бот @{me.Username} на связи");
            await _bot.SetMyCommands(
                new[]
                {
                    new BotCommand("start", "главное меню"),
                    new BotCommand("menu", "меню"),
                    new BotCommand("help", "как подключиться"),
                    new BotCommand("logout", "выйти из аккаунта"),
                },
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            Log($"не смог представиться: {ex.Message}");
        }

        try
        {
            var handshake = await _panel.HandshakeAsync(ct);
            Log($"панель ответила: {handshake.Service}, api {handshake.Api}");
        }
        catch (Exception ex)
        {
            Log($"панель пока не отвечает: {ex.Message}");
        }

        var receiver = new ReceiverOptions
        {
            AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery },
            DropPendingUpdates = false,
        };

        _bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiver, ct);
        Log("слушаю Telegram");

        await Task.Delay(Timeout.Infinite, ct);
    }

    private Task HandleErrorAsync(
        ITelegramBotClient bot, Exception exception, CancellationToken ct)
    {
        Log($"polling: {exception.Message}");
        return Task.CompletedTask;
    }

    private async Task HandleUpdateAsync(
        ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            if (update.Type == UpdateType.Message && update.Message is { } message)
            {
                if (!TryAcceptMessage(message, out var text))
                    return;

                var chat = new ChatTarget(message.Chat.Id, message.From!.Id);
                await WithLockAsync(chat.UserId, () =>
                {
                    // Пока ждали очередь этого человека, сообщение могло состариться.
                    if (BotMessageFilter.IsStale(message.Date, DateTime.UtcNow))
                        return Task.CompletedTask;
                    return HandleTextAsync(chat, text, ct);
                });
                return;
            }

            if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is { } query)
            {
                var chat = new ChatTarget(
                    query.Message?.Chat.Id ?? query.From.Id, query.From.Id);
                await WithLockAsync(chat.UserId, () => HandleCallbackAsync(chat, query, ct));
            }
        }
        catch (Exception ex)
        {
            Log($"update: {ex}");
        }
    }

    /// <summary>
    /// Пропустить ли сообщение в обработку: не от бота, не из канала, не старше
    /// минуты и адресовано ли оно боту. В группе обычная реплика не адресована.
    /// </summary>
    private bool TryAcceptMessage(Message message, out string text)
    {
        text = string.Empty;

        if (message.From is null || message.From.IsBot || message.From.Id == _botId)
            return false;

        if (!BotMessageFilter.IsSupportedChat(message.Chat.Type))
            return false;

        if (BotMessageFilter.IsStale(message.Date, DateTime.UtcNow))
            return false;

        var isReplyToBot = message.ReplyToMessage?.From?.Id == _botId;
        return BotMessageFilter.TryTakeAddressedText(
            message.Text,
            message.Chat.Type,
            isReplyToBot,
            message.Entities,
            _botUsername,
            out text);
    }

    private async Task WithLockAsync(long id, Func<Task> action)
    {
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            gate.Release();
        }
    }

    // ── текст и команды ─────────────────────────────────────────────────────

    private async Task HandleTextAsync(ChatTarget chat, string raw, CancellationToken ct)
    {
        var text = raw.Trim();
        if (text.Length == 0)
            return;

        if (text.StartsWith('/'))
        {
            var command = text.Split(' ', 2)[0].Split('@')[0].ToLowerInvariant();
            switch (command)
            {
                case "/start":
                    await OnStartAsync(chat, ct);
                    return;
                case "/menu":
                    await ShowMenuAsync(chat, null, ct);
                    return;
                case "/help":
                    await ShowHelpAsync(chat, null, ct);
                    return;
                case "/logout":
                    await ShowLogoutConfirmAsync(chat, null, ct);
                    return;
                case "/cancel":
                    State(chat.UserId).Wizard = null;
                    await ShowMenuAsync(chat, null, ct);
                    return;
                default:
                    await SendAsync(
                        chat, "Не знаю такую команду. Открой меню — /menu",
                        MenuKeyboard(), ct);
                    return;
            }
        }

        var state = State(chat.UserId);
        switch (state.Wizard)
        {
            case CreateRoomWizard { Step: "name" } create:
                await CreateRoomAsync(chat, null, create, text, ct);
                return;
            case ReportWizard { Step: "reason" } report:
                await SubmitReportAsync(chat, null, report, text == "-" ? "" : text, ct);
                return;
            case PlayerLookupWizard:
                state.Wizard = null;
                await LookupPlayerAsync(chat, null, text, ct);
                return;
        }

        // Код из лобби принимаем и когда привязка уже есть: так человек входит
        // заново или меняет игрока. Успешный вход заменит старую привязку.
        if (IsLobbyCode(text))
        {
            await TryAuthenticateAsync(chat, text, ct);
            return;
        }

        if (ActiveBinding(chat.UserId) is null)
        {
            await ShowAuthPromptAsync(chat, null, ct);
            return;
        }

        await SendAsync(
            chat,
            "Не понял. Открой меню — /menu\n" +
            "Если хочешь выйти из аккаунта: /logout",
            MenuKeyboard(), ct);
    }

    private async Task OnStartAsync(ChatTarget chat, CancellationToken ct)
    {
        State(chat.UserId).Wizard = null;
        if (ActiveBinding(chat.UserId) is not null)
        {
            await ShowMenuAsync(chat, null, ct);
            return;
        }

        await ShowAuthPromptAsync(chat, null, ct);
    }

    private static bool IsLobbyCode(string text) =>
        text.Length == 6 && text.All(char.IsAsciiDigit);

    private async Task TryAuthenticateAsync(ChatTarget chat, string code, CancellationToken ct)
    {
        try
        {
            await _bot.SendChatAction(chat.ChatId, ChatAction.Typing, cancellationToken: ct);
        }
        catch
        {
            // «печатает» — украшение, а не условие входа.
        }

        PanelApiException? failure = null;
        var unreachable = false;

        try
        {
            var result = await _panel.AuthenticateAsync(code, ct);
            _store.Upsert(
                chat.UserId, result.Profile.Id, result.Profile.Nick, result.Token);
            State(chat.UserId).Wizard = null;

            await ShowMenuAsync(
                chat, null, ct,
                $"✅ Ты в игре как <b>{Fmt.E(result.Profile.Nick)}</b>.\n" +
                $"Сервер: {Fmt.E(_config.Label)}.");
            return;
        }
        catch (PanelApiException ex) when (ex.Code == "bad_code")
        {
            // Код не подошёл — ниже скажем об этом одним текстом.
        }
        catch (PanelApiException ex) when (ex.IsBotKeyProblem)
        {
            Log($"auth: ключ бота не принят ({ex.Code})");
            await SendAsync(
                chat,
                "Сервер не принял ключ бота.\n" +
                "Владельцу: проверь <code>BOT_KEY</code> в <code>.env</code> и " +
                "перезапусти бота.\n" +
                "Новый ключ выдаёт <code>tgbot resetkey</code>.",
                AuthKeyboard(), ct);
            return;
        }
        catch (PanelApiException ex) when (ex.Code == "banned")
        {
            await SendAsync(
                chat,
                $"Аккаунт забанен на сервере «{Fmt.E(_config.Label)}». Вход закрыт.",
                AuthKeyboard(), ct);
            return;
        }
        catch (PanelApiException ex)
        {
            Log($"auth: {ex.Code}: {ex.Message}");
            failure = ex;
        }
        catch (Exception ex)
        {
            Log($"auth: {ex.Message}");
            unreachable = true;
        }

        if (unreachable)
        {
            await SendAsync(
                chat,
                $"Не смог проверить код: сервер «{Fmt.E(_config.Label)}» не отвечает.\n" +
                "Попробуй ещё раз через минуту.",
                AuthKeyboard(), ct);
            return;
        }

        if (failure is not null)
        {
            await SendAsync(
                chat,
                $"Не получилось войти: {Fmt.E(failure.Message)}",
                AuthKeyboard(), ct);
            return;
        }

        await SendAsync(
            chat,
            "Код не подошёл или уже истёк.\n" +
            "Зайди в лобби ещё раз — там будет новый код из 6 цифр.",
            AuthKeyboard(), ct);
    }

    // ── меню ────────────────────────────────────────────────────────────────

    private async Task ShowMenuAsync(
        ChatTarget chat, int? messageId, CancellationToken ct, string? prefix = null)
    {
        var binding = await RequireBindingAsync(chat, messageId, ct);
        if (binding is null)
            return;

        var text = Fmt.JoinLines(
            prefix,
            "<b>влалChillow</b>",
            $"Ты в игре как <b>{Fmt.E(binding.Nick)}</b> (ID {binding.PlayerId}).",
            $"Сервер: {Fmt.E(_config.Label)}",
            "",
            "Выбери, что нужно:");

        await EditOrSendAsync(chat, messageId, text, MenuKeyboard(), ct);
    }

    private static InlineKeyboardMarkup MenuKeyboard() => new(new[]
    {
        new[]
        {
            Btn("🟢 Статус", "status"),
            Btn("🎮 Комнаты", "rooms:1"),
        },
        new[]
        {
            Btn("➕ Создать", "create"),
            Btn("👤 Профиль", "profile"),
        },
        new[]
        {
            Btn("🏆 Матчи", "matches:0:1"),
            Btn("🔍 Игрок", "player"),
        },
        new[]
        {
            Btn("🚨 Мои жалобы", "reports:1"),
            Btn("👑 Админы", "admins"),
        },
        new[]
        {
            Btn("📖 Инструкция", "help"),
            Btn("🚪 Выйти", "logout"),
        },
    });

    private static InlineKeyboardMarkup AuthKeyboard() => new(new[]
    {
        new[] { Btn("📖 Инструкция", "help") },
    });

    private static InlineKeyboardMarkup RetryKeyboard(string retryData) => new(new[]
    {
        new[]
        {
            Btn("🔄 Повторить", retryData),
            Btn("🏠 Меню", "menu"),
        },
    });

    private static InlineKeyboardButton Btn(string text, string data) => new(text, data);

    private async Task ShowAuthPromptAsync(
        ChatTarget chat, int? messageId, CancellationToken ct, string? prefix = null)
    {
        var text =
            (string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix + "\n\n") +
            "👋 Привет! Это бот <b>влалChillow</b>.\n\n" +
            "Чтобы войти:\n" +
            "1. Запусти игру и зайди в лобби.\n" +
            "2. Возьми там код из 6 цифр.\n" +
            "3. Пришли код сюда.\n\n" +
            $"Сервер: {Fmt.E(_config.Label)}\n" +
            $"Адрес: <code>{Fmt.E(_config.GameAddress)}</code>\n\n" +
            "Код живёт 30 минут. Бот запомнит тебя и не будет спрашивать каждый раз.";

        if (chat.IsGroup)
        {
            text += "\n\n🔒 Код из лобби лучше присылать в личку боту — " +
                    "в группе его увидят все.";
            text += _botUsername is null
                ? "\nЕсли всё же здесь — ответь на это сообщение."
                : $"\nЕсли всё же здесь — ответь на это сообщение или упомяни @{_botUsername}.";
        }

        await EditOrSendAsync(chat, messageId, text, AuthKeyboard(), ct);
    }

    // ── статус ──────────────────────────────────────────────────────────────

    private async Task ShowStatusAsync(ChatTarget chat, int? messageId, CancellationToken ct)
    {
        try
        {
            var status = await _panel.GetStatusAsync(ct);
            var text = Fmt.JoinLines(
                $"🟢 <b>{Fmt.E(_config.Label)}</b> · в сети",
                $"В лобби: {status.OnlineLobby}",
                $"В бою: {status.OnlineMatch}",
                $"Комнат: {status.RoomsCount} / {status.RoomsMax}",
                status.MatchRunning && status.Mode.Length > 0
                    ? $"Идёт катка: {Fmt.E(status.Mode)}"
                    : null,
                status.GamePortFailed
                    ? "⚠️ Игровой порт не слушается — проверь сервер."
                    : !status.GameListening
                        ? "Игровой порт ждёт следующей комнаты."
                        : null,
                "",
                $"Адрес: <code>{Fmt.E(_config.GameAddress)}</code>");

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    Btn("🔄 Обновить", "status"),
                    Btn("🏠 Меню", "menu"),
                },
            });

            await EditOrSendAsync(chat, messageId, text, keyboard, ct);
        }
        catch (PanelApiException ex)
        {
            await HandlePanelErrorAsync(chat, messageId, ex, "status", ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chat, messageId, ex, "status", ct);
        }
    }

    // ── комнаты ─────────────────────────────────────────────────────────────

    private async Task ShowRoomsAsync(
        ChatTarget chat, int? messageId, int page, CancellationToken ct)
    {
        var binding = ActiveBinding(chat.UserId);
        try
        {
            var response = await _panel.GetRoomsAsync(binding?.SessionToken, ct);

            var rooms = response.Rooms
                .OrderByDescending(room => room.IsMine)
                .ThenBy(room => room.Number)
                .ToList();

            const int pageSize = 5;
            var pages = Math.Max(1, (rooms.Count + pageSize - 1) / pageSize);
            page = Math.Clamp(page, 1, pages);

            var text = new StringBuilder();
            text.AppendLine($"🎮 <b>Комнаты</b> · {rooms.Count} / {response.MaxRooms}");
            text.AppendLine($"Сервер: {Fmt.E(_config.Label)}");
            if (rooms.Count == 0)
            {
                text.AppendLine();
                text.AppendLine("Сейчас комнат нет. Создай свою — игроки увидят её в списке.");
            }
            else
            {
                text.AppendLine();
                text.AppendLine("Нажми на комнату, чтобы открыть карточку.");
            }

            var rows = new List<InlineKeyboardButton[]>();
            foreach (var room in rooms.Skip((page - 1) * pageSize).Take(pageSize))
            {
                var mark = room.IsMine ? "⭐ " : "";
                var label =
                    $"{mark}#{room.Number} · {Fmt.Short(room.ModeName, 12)} · " +
                    $"{room.Players}/{room.MaxPlayers}";
                rows.Add(new[] { Btn(label, $"room:{room.Number}") });
            }

            if (pages > 1)
            {
                var nav = new List<InlineKeyboardButton>();
                if (page > 1)
                    nav.Add(Btn("⬅️", $"rooms:{page - 1}"));
                nav.Add(Btn($"{page}/{pages}", $"rooms:{page}"));
                if (page < pages)
                    nav.Add(Btn("➡️", $"rooms:{page + 1}"));
                rows.Add(nav.ToArray());
            }

            rows.Add(new[]
            {
                Btn("➕ Создать", "create"),
                Btn("🔄 Обновить", $"rooms:{page}"),
            });
            rows.Add(new[] { Btn("🏠 Меню", "menu") });

            await EditOrSendAsync(
                chat, messageId, text.ToString().TrimEnd(),
                new InlineKeyboardMarkup(rows), ct);
        }
        catch (PanelApiException ex)
        {
            await HandlePanelErrorAsync(chat, messageId, ex, $"rooms:{page}", ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chat, messageId, ex, $"rooms:{page}", ct);
        }
    }

    private async Task ShowRoomAsync(
        ChatTarget chat, int? messageId, int number, CancellationToken ct)
    {
        try
        {
            var binding = await RequireBindingAsync(chat, messageId, ct);
            if (binding is null)
                return;

            var room = await _panel.GetRoomAsync(number, ct);
            var mine = room.Owner.Id == binding.PlayerId;

            var text = new StringBuilder();
            text.AppendLine($"🎮 <b>Комната #{room.Number}</b>");
            text.AppendLine($"Сервер: {Fmt.E(_config.Label)}");
            text.AppendLine($"{Fmt.State(room.State)} · {Fmt.E(room.ModeName)}");
            text.AppendLine($"Карта: {Fmt.E(room.Map)}");
            text.AppendLine($"Название: {Fmt.E(room.Name)}");
            text.AppendLine(
                $"Владелец: {Fmt.E(room.Owner.Nick)} ({Fmt.E(room.Owner.DisplayId)})");
            text.AppendLine(
                $"Игроки: {room.PlayerCount}/{room.MaxPlayers} · {Fmt.E(room.TeamLabel)}");
            text.AppendLine($"Счёт: CT {room.ScoreCt} : {room.ScoreTr} T · раунд {room.Round}");

            if (room.Players.Count > 0)
            {
                text.AppendLine();
                foreach (var group in room.Players
                             .GroupBy(player => player.Team)
                             .OrderBy(group => TeamOrder(group.Key)))
                {
                    text.AppendLine($"<b>{Fmt.Team(group.Key)}</b>");
                    foreach (var player in group.OrderByDescending(p => p.Kills))
                    {
                        text.Append("• ");
                        text.Append(Fmt.E(player.Nick));
                        text.Append($" — {player.Kills}/{player.Deaths}");
                        if (player.Left)
                            text.Append(" (вышел)");
                        text.AppendLine();
                    }
                }
            }

            if (room.MatchId is { } matchId)
                text.AppendLine($"\n🏆 Итоги: матч #{matchId}");

            var rows = new List<InlineKeyboardButton[]>
            {
                new[] { Btn("🔄 Обновить", $"room:{room.Number}") },
            };

            if (mine && room.State != "finished")
                rows.Add(new[] { Btn("🛑 Завершить", $"roomend:{room.Number}") });

            if (room.MatchId is { } id)
                rows.Add(new[]
                {
                    Btn("🏆 Итоги", $"match:{id}:{binding.PlayerId}"),
                });

            rows.Add(new[]
            {
                Btn("⬅️ Комнаты", "rooms:1"),
                Btn("🏠 Меню", "menu"),
            });

            await EditOrSendAsync(
                chat, messageId, text.ToString().TrimEnd(),
                new InlineKeyboardMarkup(rows), ct);
        }
        catch (PanelApiException ex)
        {
            await HandlePanelErrorAsync(chat, messageId, ex, $"room:{number}", ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chat, messageId, ex, $"room:{number}", ct);
        }
    }

    private static int TeamOrder(string? team) => team switch
    {
        "CT" => 0,
        "T" => 1,
        "SPEC" => 2,
        _ => 3,
    };

    private async Task ShowEndConfirmAsync(
        ChatTarget chat, int? messageId, int number, CancellationToken ct)
    {
        var text =
            $"Завершить комнату <b>#{number}</b>?\n\n" +
            "Катка закончится, игроки вернутся в лобби.";

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { Btn("Да, завершить", $"roomend:{number}:yes") },
            new[] { Btn("Нет", $"room:{number}") },
        });

        await EditOrSendAsync(chat, messageId, text, keyboard, ct);
    }

    private async Task EndRoomAsync(
        ChatTarget chat, int? messageId, int number, CancellationToken ct)
    {
        var binding = await RequireBindingAsync(chat, messageId, ct);
        if (binding is null)
            return;

        try
        {
            await _panel.EndRoomAsync(binding.SessionToken, number, ct);
            var text =
                $"✅ Комната <b>#{number}</b> завершена.\n" +
                "Игроки вернулись в лобби.";
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    Btn("🎮 Комнаты", "rooms:1"),
                    Btn("🏠 Меню", "menu"),
                },
            });
            await EditOrSendAsync(chat, messageId, text, keyboard, ct);
        }
        catch (PanelApiException ex)
        {
            await HandlePanelErrorAsync(chat, messageId, ex, $"room:{number}", ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chat, messageId, ex, $"room:{number}", ct);
        }
    }

    // ── создание комнаты ────────────────────────────────────────────────────

    private async Task ShowCreateModesAsync(
        ChatTarget chat, int? messageId, CancellationToken ct)
    {
        var binding = await RequireBindingAsync(chat, messageId, ct);
        if (binding is null)
            return;

        try
        {
            var modes = (await _panel.GetModesAsync(ct)).Modes
                .Where(mode => mode.Implemented)
                .ToList();

            if (modes.Count == 0)
            {
                await EditOrSendAsync(
                    chat, messageId,
                    "Сервер не отдал ни одного режима. Попробуй позже.",
                    RetryKeyboard("create"), ct);
                return;
            }

            State(chat.UserId).Wizard = new CreateRoomWizard
            {
                Step = "mode",
                Modes = modes,
            };

            var rows = new List<InlineKeyboardButton[]>();
            for (var i = 0; i < modes.Count; i += 2)
            {
                var row = new List<InlineKeyboardButton>
                {
                    Btn(Fmt.Short(modes[i].Name, 20), $"create:mode:{modes[i].Id}"),
                };
                if (i + 1 < modes.Count)
                    row.Add(Btn(Fmt.Short(modes[i + 1].Name, 20), $"create:mode:{modes[i + 1].Id}"));
                rows.Add(row.ToArray());
            }

            rows.Add(new[] { Btn("⬅️ Отмена", "menu") });

            await EditOrSendAsync(
                chat, messageId,
                "➕ <b>Новая комната</b>\n\nВыбери режим. Показаны только те, что реально запускаются.",
                new InlineKeyboardMarkup(rows), ct);
        }
        catch (PanelApiException ex)
        {
            await HandlePanelErrorAsync(chat, messageId, ex, "create", ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chat, messageId, ex, "create", ct);
        }
    }

    private async Task ShowCreateMapsAsync(
        ChatTarget chat, int? messageId, string modeId, CancellationToken ct)
    {
        var binding = await RequireBindingAsync(chat, messageId, ct);
        if (binding is null)
            return;

        try
        {
            var state = State(chat.UserId);
            var wizard = state.Wizard as CreateRoomWizard;
            GameMode? mode = wizard?.Mode;

            if (mode is null || !mode.Id.Equals(modeId, StringComparison.Ordinal))
            {
                var modes = (await _panel.GetModesAsync(ct)).Modes;
                mode = modes.FirstOrDefault(m => m.Id.Equals(modeId, StringComparison.Ordinal));
                if (mode is null)
                {
                    await ShowCreateModesAsync(chat, messageId, ct);
                    return;
                }

                wizard = new CreateRoomWizard { Modes = modes };
                state.Wizard = wizard;
            }

            if (wizard is null)
            {
                wizard = new CreateRoomWizard();
                state.Wizard = wizard;
            }

            wizard.Mode = mode;
            wizard.Step = "map";

            var rows = new List<InlineKeyboardButton[]>();
            for (var i = 0; i < mode.Levels.Count; i += 2)
            {
                var row = new List<InlineKeyboardButton>
                {
                    Btn(Fmt.Short(mode.Levels[i], 18), $"create:map:{i}"),
                };
                if (i + 1 < mode.Levels.Count)
                    row.Add(Btn(Fmt.Short(mode.Levels[i + 1], 18), $"create:map:{i + 1}"));
                rows.Add(row.ToArray());
            }

            rows.Add(new[]
            {
                Btn("⬅️ Назад", "create"),
                Btn("🏠 Меню", "menu"),
            });

            await EditOrSendAsync(
                chat, messageId,
                $"➕ <b>Новая комната</b>\n\nРежим: <b>{Fmt.E(mode.Name)}</b>\nВыбери карту.",
                new InlineKeyboardMarkup(rows), ct);
        }
        catch (PanelApiException ex)
        {
            await HandlePanelErrorAsync(chat, messageId, ex, "create", ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chat, messageId, ex, "create", ct);
        }
    }

    private async Task ShowCreateTeamsAsync(
        ChatTarget chat, int? messageId, string modeId, int levelIndex, CancellationToken ct)
    {
        var state = State(chat.UserId);
        var wizard = state.Wizard as CreateRoomWizard;
        var mode = wizard?.Mode;
        if (wizard is null || mode is null
            || !mode.Id.Equals(modeId, StringComparison.Ordinal)
            || levelIndex < 0 || levelIndex >= mode.Levels.Count)
        {
            await ShowCreateMapsAsync(chat, messageId, modeId, ct);
            return;
        }

        wizard.Level = mode.Levels[levelIndex];

        if (mode.TeamSizes.Count <= 1)
        {
            wizard.PerTeam = mode.TeamSizes.FirstOrDefault(mode.PerTeam);
            await ShowCreateNameAsync(chat, messageId, ct);
            return;
        }

        var rows = new List<InlineKeyboardButton[]>();
        foreach (var size in mode.TeamSizes)
        {
            rows.Add(new[]
            {
                Btn($"{size} на {size}", $"create:team:{size}"),
            });
        }

        rows.Add(new[]
        {
            Btn("⬅️ Назад", $"create:mode:{mode.Id}"),
            Btn("🏠 Меню", "menu"),
        });

        await EditOrSendAsync(
            chat, messageId,
            $"➕ <b>Новая комната</b>\n\n" +
            $"Режим: <b>{Fmt.E(mode.Name)}</b>\n" +
            $"Карта: <b>{Fmt.E(wizard.Level)}</b>\n\n" +
            "Сколько игроков в команде?",
            new InlineKeyboardMarkup(rows), ct);
    }

    private async Task ShowCreateNameAsync(
        ChatTarget chat, int? messageId, CancellationToken ct)
    {
        var state = State(chat.UserId);
        if (state.Wizard is not CreateRoomWizard current
            || current.Mode is null
            || current.Level is null)
        {
            await ShowCreateModesAsync(chat, messageId, ct);
            return;
        }

        var mode = current.Mode;
        var level = current.Level;
        state.Wizard = new CreateRoomWizard
        {
            Step = "name",
            Modes = current.Modes,
            Mode = mode,
            Level = level,
            PerTeam = current.PerTeam,
        };

        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { Btn("Без названия", "create:noname") },
            new[] { Btn("⬅️ Назад", $"create:mode:{mode.Id}") },
            new[] { Btn("🏠 Меню", "menu") },
        };

        await EditOrSendAsync(
            chat, messageId,
            $"➕ <b>Новая комната</b>\n\n" +
            $"Режим: <b>{Fmt.E(mode.Name)}</b>\n" +
            $"Карта: <b>{Fmt.E(level)}</b>\n\n" +
            "Пришли название комнаты (до 32 символов) или нажми «Без названия»." +
            ReplyHint(chat),
            new InlineKeyboardMarkup(rows), ct);
    }

    private async Task CreateRoomAsync(
        ChatTarget chat, int? messageId, CreateRoomWizard wizard, string rawName,
        CancellationToken ct)
    {
        var binding = await RequireBindingAsync(chat, messageId, ct);
        if (binding is null)
            return;

        if (wizard.Mode is null || wizard.Level is null)
        {
            await ShowCreateModesAsync(chat, messageId, ct);
            return;
        }

        var name = rawName.Trim();
        if (name.Length > 32)
            name = name[..32];

        try
        {
            var created = await _panel.CreateRoomAsync(
                binding.SessionToken,
                new CreateRoomRequest
                {
                    Mode = wizard.Mode.Id,
                    Level = wizard.Level,
                    Name = name,
                    PerTeam = wizard.PerTeam,
                },
                ct);

            State(chat.UserId).Wizard = null;

            var text = Fmt.JoinLines(
                $"✅ <b>Комната #{created.Number} создана</b>",
                $"{Fmt.E(created.ModeName)} · {Fmt.E(created.Level)}",
                string.IsNullOrWhiteSpace(created.Name)
                    ? null
                    : $"Название: {Fmt.E(created.Name)}",
                $"Размер: {Fmt.E(created.TeamLabel)}",
                "",
                "Команда для входа:",
                $"<code>{Fmt.E(created.JoinCommand)}</code>",
                "",
                "Скажи её игрокам или отправь в чат.");

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { Btn("🎮 Открыть комнату", $"room:{created.Number}") },
                new[] { Btn("🏠 Меню", "menu") },
            });

            await EditOrSendAsync(chat, messageId, text, keyboard, ct);
        }
        catch (PanelApiException ex)
        {
            var text = ex.Code switch
            {
                "player_offline" =>
                    "Тебя нет в лобби.\nЗайди в игру, дождись лобби и создай комнату ещё раз.",
                "one_room_per_player" =>
                    "У тебя уже есть комната. Сначала заверши её.",
                "too_many_rooms" or "room_limit" =>
                    "На сервере сейчас нет места для новой комнаты.",
                "unknown_mode" or "unknown_level" or "unknown_team_size" =>
                    "Сервер не принял параметры комнаты. Обнови каталог и попробуй снова.",
                _ => $"Не получилось создать комнату: {Fmt.E(ex.Message)}",
            };

            var keyboard = ex.Code == "one_room_per_player"
                ? new InlineKeyboardMarkup(new[]
                {
                    new[] { Btn("🎮 Комнаты", "rooms:1") },
                    new[] { Btn("🏠 Меню", "menu") },
                })
                : RetryKeyboard("create");

            await EditOrSendAsync(chat, messageId, text, keyboard, ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chat, messageId, ex, "create", ct);
        }
    }

    // ── привязка ────────────────────────────────────────────────────────────

    private UserBinding? ActiveBinding(long userId) => _store.Get(userId);

    // ── общие ответы ────────────────────────────────────────────────────────

    private async Task<UserBinding?> RequireBindingAsync(
        ChatTarget chat, int? messageId, CancellationToken ct)
    {
        var binding = ActiveBinding(chat.UserId);
        if (binding is not null && !string.IsNullOrWhiteSpace(binding.SessionToken))
            return binding;

        await ShowAuthPromptAsync(chat, messageId, ct);
        return null;
    }

    private async Task HandlePanelErrorAsync(
        ChatTarget chat, int? messageId, PanelApiException ex,
        string? retryData, CancellationToken ct)
    {
        var binding = ActiveBinding(chat.UserId);
        if (ex.IsAuthLost)
        {
            if (binding is not null)
                _store.Delete(chat.UserId);
            await ShowAuthPromptAsync(
                chat, messageId, ct,
                "🔒 Сессия истекла. Зайди в лобби и пришли новый код из 6 цифр.");
            return;
        }

        var text = ex.Code switch
        {
            "bad_bot_key" or "bot_disabled" or "bot_key_unavailable" =>
                $"Сервер «{Fmt.E(_config.Label)}» не принял ключ бота.\n" +
                "Владельцу: проверь <code>BOT_KEY</code> в <code>.env</code>; " +
                "новый выдаёт <code>tgbot resetkey</code>.",
            "player_offline" =>
                "Тебя нет в лобби. Зайди в игру и попробуй снова.",
            "banned" =>
                "Аккаунт забанен. Это действие недоступно.",
            "rate_limited" or "daily_limit" or "cooldown" =>
                ex.RetryAfterSec > 0
                    ? $"{Fmt.E(ex.Message)}\nПовтори через {ex.RetryAfterSec} с."
                    : Fmt.E(ex.Message),
            _ => Fmt.E(ex.Message),
        };

        var keyboard = retryData is null ? MenuKeyboard() : RetryKeyboard(retryData);
        await EditOrSendAsync(chat, messageId, text, keyboard, ct);
    }

    private async Task NetworkErrorAsync(
        ChatTarget chat, int? messageId, Exception ex,
        string? retryData, CancellationToken ct)
    {
        Log($"сеть: {ex.Message}");
        var text =
            $"Сервер «{Fmt.E(_config.Label)}» не отвечает.\n" +
            "Попробуй ещё раз через минуту.";
        var keyboard = retryData is null ? MenuKeyboard() : RetryKeyboard(retryData);
        await EditOrSendAsync(chat, messageId, text, keyboard, ct);
    }

    private async Task SendAsync(
        ChatTarget chat, string text, InlineKeyboardMarkup? keyboard, CancellationToken ct)
    {
        await _limiter.WaitAsync(chat.ChatId, chat.IsGroup, ct);
        await SendCoreAsync(chat, text, keyboard, ct);
    }

    private async Task SendCoreAsync(
        ChatTarget chat, string text, InlineKeyboardMarkup? keyboard, CancellationToken ct)
    {
        await _bot.SendMessage(
            chat.ChatId, text,
            parseMode: ParseMode.Html,
            replyMarkup: keyboard,
            cancellationToken: ct);
    }

    private async Task EditOrSendAsync(
        ChatTarget chat, int? messageId, string text,
        InlineKeyboardMarkup? keyboard, CancellationToken ct)
    {
        // Правка тоже расходует лимит Telegram, поэтому ждём один раз на весь вызов:
        // если правка не удалась и ниже уйдёт новое сообщение, второй паузы не будет.
        await _limiter.WaitAsync(chat.ChatId, chat.IsGroup, ct);

        if (messageId is { } id)
        {
            try
            {
                await _bot.EditMessageText(
                    chat.ChatId, id, text,
                    parseMode: ParseMode.Html,
                    replyMarkup: keyboard,
                    cancellationToken: ct);
                return;
            }
            catch (ApiRequestException ex)
                when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            catch (ApiRequestException)
            {
                // Сообщение могли удалить — ниже отправим новое.
            }
        }

        await SendCoreAsync(chat, text, keyboard, ct);
    }

    private UserState State(long userId) =>
        _states.GetOrAdd(userId, _ => new UserState());

    /// <summary>
    /// Кому отвечаем и от чьего имени действуем. В личке это одно и то же число,
    /// а в группе — разные: сообщение уходит в группу, но привязка, визарды и
    /// лимиты принадлежат конкретному человеку, а не всей группе.
    /// </summary>
    private readonly record struct ChatTarget(long ChatId, long UserId)
    {
        public bool IsGroup => ChatId < 0;
    }

    private static void Log(string message) =>
        Console.WriteLine($"[bot] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} {message}");

    // ── состояние в памяти ──────────────────────────────────────────────────

    private sealed class UserState
    {
        public Wizard? Wizard { get; set; }
    }

    private abstract class Wizard
    {
    }

    private sealed class CreateRoomWizard : Wizard
    {
        public string Step { get; set; } = "mode";
        public List<GameMode> Modes { get; set; } = new();
        public GameMode? Mode { get; set; }
        public string? Level { get; set; }
        public int PerTeam { get; set; }
    }

    private sealed class ReportWizard : Wizard
    {
        public int TargetId { get; set; }
        public string? TargetNick { get; set; }
        public string? Category { get; set; }
        public string Step { get; set; } = "category";
    }

    private sealed class PlayerLookupWizard : Wizard
    {
    }
}
