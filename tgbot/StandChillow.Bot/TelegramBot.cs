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
/// из SQLite по паре (Telegram ID, сервер). Поэтому два человека могут одновременно
/// сидеть в боте и не видеть чужие данные.
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
    private readonly IReadOnlyDictionary<string, PanelClient> _panels;
    private readonly ITelegramBotClient _bot;
    private readonly ConcurrentDictionary<long, UserState> _states = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();

    public TelegramBot(
        BotConfig config, BotStore store, IReadOnlyDictionary<string, PanelClient> panels)
    {
        _config = config;
        _store = store;
        _panels = panels;

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
        foreach (var server in _config.Servers)
        {
            Log($"сервер {server.Id} ({server.Label}): панель {server.ApiBaseUrl}, " +
                $"игра {server.GameAddress}");
        }

        Log($"база: {_config.DbPath}");

        try
        {
            var me = await _bot.GetMe(cancellationToken: ct);
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

        foreach (var server in _config.Servers)
        {
            try
            {
                var handshake = await PanelOf(server).HandshakeAsync(ct);
                Log($"панель {server.Id} ответила: {handshake.Service}, api {handshake.Api}");
            }
            catch (Exception ex)
            {
                Log($"панель {server.Id} пока не отвечает: {ex.Message}");
            }
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
            if (update.Type == UpdateType.Message && update.Message is { Text: { } text } message)
            {
                var chatId = message.Chat.Id;
                await WithLockAsync(chatId, () => HandleTextAsync(chatId, text, ct));
                return;
            }

            if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is { } query)
            {
                var chatId = query.Message?.Chat.Id ?? query.From.Id;
                await WithLockAsync(chatId, () => HandleCallbackAsync(chatId, query, ct));
            }
        }
        catch (Exception ex)
        {
            Log($"update: {ex}");
        }
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

    private async Task HandleTextAsync(long chatId, string raw, CancellationToken ct)
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
                    await OnStartAsync(chatId, ct);
                    return;
                case "/menu":
                    await ShowMenuAsync(chatId, null, ct);
                    return;
                case "/help":
                    await ShowHelpAsync(chatId, null, ct);
                    return;
                case "/logout":
                    await ShowLogoutConfirmAsync(chatId, null, ct);
                    return;
                case "/cancel":
                    State(chatId).Wizard = null;
                    await ShowMenuAsync(chatId, null, ct);
                    return;
                default:
                    await SendAsync(
                        chatId, "Не знаю такую команду. Открой меню — /menu",
                        MenuKeyboard(), ct);
                    return;
            }
        }

        var state = State(chatId);
        switch (state.Wizard)
        {
            case CreateRoomWizard { Step: "name" } create:
                await CreateRoomAsync(chatId, null, create, text, ct);
                return;
            case ReportWizard { Step: "reason" } report:
                await SubmitReportAsync(chatId, null, report, text == "-" ? "" : text, ct);
                return;
            case PlayerLookupWizard:
                state.Wizard = null;
                await LookupPlayerAsync(chatId, null, text, ct);
                return;
        }

        var binding = ActiveBinding(chatId);
        if (binding is null)
        {
            if (IsLobbyCode(text))
            {
                await TryAuthenticateAsync(chatId, text, ct);
                return;
            }

            await ShowAuthPromptAsync(chatId, null, ct);
            return;
        }

        await SendAsync(
            chatId,
            "Не понял. Открой меню — /menu\n" +
            "Если хочешь выйти из аккаунта: /logout",
            MenuKeyboard(), ct);
    }

    private async Task OnStartAsync(long chatId, CancellationToken ct)
    {
        State(chatId).Wizard = null;
        if (ActiveBinding(chatId) is not null)
        {
            await ShowMenuAsync(chatId, null, ct);
            return;
        }

        await ShowAuthPromptAsync(chatId, null, ct);
    }

    private static bool IsLobbyCode(string text) =>
        text.Length == 6 && text.All(char.IsAsciiDigit);

    private async Task TryAuthenticateAsync(long chatId, string code, CancellationToken ct)
    {
        try
        {
            await _bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
        }
        catch
        {
            // «печатает» — украшение, а не условие входа.
        }

        var keyProblems = new List<ServerProfile>();
        var unreachable = new List<ServerProfile>();
        var otherFailures = new List<string>();

        // Код принадлежит тому серверу, где игрок стоит в лобби. Проверяем все
        // настроенные серверы по очереди: где приняли — туда и привязываемся.
        foreach (var server in _config.Servers)
        {
            try
            {
                var result = await PanelOf(server).AuthenticateAsync(code, ct);
                _store.Upsert(
                    chatId, server.Id,
                    result.Profile.Id, result.Profile.Nick, result.Token);
                _store.DeleteOthers(chatId, server.Id);
                State(chatId).Wizard = null;

                await ShowMenuAsync(
                    chatId, null, ct,
                    $"✅ Ты в игре как <b>{Fmt.E(result.Profile.Nick)}</b>.\n" +
                    $"Сервер: {Fmt.E(server.Label)}.");
                return;
            }
            catch (PanelApiException ex) when (ex.Code == "bad_code")
            {
                // Код не от этого сервера — проверяем следующий.
            }
            catch (PanelApiException ex) when (ex.IsBotKeyProblem)
            {
                Log($"auth {server.Id}: ключ бота не принят ({ex.Code})");
                keyProblems.Add(server);
            }
            catch (PanelApiException ex) when (ex.Code == "banned")
            {
                await SendAsync(
                    chatId,
                    $"Аккаунт забанен на сервере «{Fmt.E(server.Label)}». Вход закрыт.",
                    AuthKeyboard(), ct);
                return;
            }
            catch (PanelApiException ex)
            {
                Log($"auth {server.Id}: {ex.Code}: {ex.Message}");
                otherFailures.Add($"{server.Label}: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log($"auth {server.Id}: {ex.Message}");
                unreachable.Add(server);
            }
        }

        if (keyProblems.Count > 0)
        {
            var lines = keyProblems.Select(server =>
                $"• {Fmt.E(server.Label)} — <code>{Fmt.E(server.KeyEnvName)}</code>");
            await SendAsync(
                chatId,
                "Сервер не принял ключ бота.\n" +
                string.Join("\n", lines) + "\n\n" +
                "Владельцу: проверь ключ в <code>.env</code> и перезапусти бота.\n" +
                "Новый ключ выдаёт <code>tgbot resetkey</code>.",
                AuthKeyboard(), ct);
            return;
        }

        if (unreachable.Count > 0)
        {
            var names = string.Join(", ", unreachable.Select(server => server.Label));
            await SendAsync(
                chatId,
                $"Не смог проверить код: {Fmt.E(names)} не отвечает.\n" +
                "Попробуй ещё раз через минуту.",
                AuthKeyboard(), ct);
            return;
        }

        if (otherFailures.Count > 0)
        {
            await SendAsync(
                chatId,
                $"Не получилось войти: {Fmt.E(otherFailures[0])}",
                AuthKeyboard(), ct);
            return;
        }

        await SendAsync(
            chatId,
            "Код не подошёл или уже истёк.\n" +
            "Зайди в лобби ещё раз — там будет новый код из 6 цифр.",
            AuthKeyboard(), ct);
    }

    // ── меню ────────────────────────────────────────────────────────────────

    private async Task ShowMenuAsync(
        long chatId, int? messageId, CancellationToken ct, string? prefix = null)
    {
        var binding = await RequireBindingAsync(chatId, messageId, ct);
        if (binding is null)
            return;

        var text = Fmt.JoinLines(
            prefix,
            "<b>влалChillow</b>",
            $"Ты в игре как <b>{Fmt.E(binding.Nick)}</b> (ID {binding.PlayerId}).",
            $"Сервер: {Fmt.E(ProfileOf(binding)?.Label ?? binding.ServerId)}",
            "",
            "Выбери, что нужно:");

        await EditOrSendAsync(chatId, messageId, text, MenuKeyboard(), ct);
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
        long chatId, int? messageId, CancellationToken ct, string? prefix = null)
    {
        var addresses = string.Join("\n", _config.Servers.Select(server =>
            $"• {Fmt.E(server.Label)} — <code>{Fmt.E(server.GameAddress)}</code>"));

        var text =
            (string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix + "\n\n") +
            "👋 Привет! Это бот <b>влалChillow</b>.\n\n" +
            "Чтобы войти:\n" +
            "1. Запусти игру и зайди в лобби.\n" +
            "2. Возьми там код из 6 цифр.\n" +
            "3. Пришли код сюда.\n\n" +
            "Серверы:\n" + addresses + "\n\n" +
            "Код живёт 30 минут. Бот запомнит тебя и не будет спрашивать каждый раз.";

        await EditOrSendAsync(chatId, messageId, text, AuthKeyboard(), ct);
    }

    // ── статус ──────────────────────────────────────────────────────────────

    private async Task ShowStatusAsync(long chatId, int? messageId, CancellationToken ct)
    {
        var server = SelectedServer(chatId);
        try
        {
            var status = await PanelOf(server).GetStatusAsync(ct);
            var text = Fmt.JoinLines(
                $"🟢 <b>{Fmt.E(server.Label)}</b> · в сети",
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
                $"Адрес: <code>{Fmt.E(server.GameAddress)}</code>");

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    Btn("🔄 Обновить", "status"),
                    Btn("🏠 Меню", "menu"),
                },
            });

            await EditOrSendAsync(chatId, messageId, text, keyboard, ct);
        }
        catch (PanelApiException ex)
        {
            await HandlePanelErrorAsync(chatId, messageId, ex, "status", ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chatId, messageId, ex, "status", ct);
        }
    }

    // ── комнаты ─────────────────────────────────────────────────────────────

    private async Task ShowRoomsAsync(
        long chatId, int? messageId, int page, CancellationToken ct)
    {
        var server = SelectedServer(chatId);
        var binding = ActiveBinding(chatId);
        try
        {
            var response = await PanelOf(server).GetRoomsAsync(binding?.SessionToken, ct);

            var rooms = response.Rooms
                .OrderByDescending(room => room.IsMine)
                .ThenBy(room => room.Number)
                .ToList();

            const int pageSize = 5;
            var pages = Math.Max(1, (rooms.Count + pageSize - 1) / pageSize);
            page = Math.Clamp(page, 1, pages);

            var text = new StringBuilder();
            text.AppendLine($"🎮 <b>Комнаты</b> · {rooms.Count} / {response.MaxRooms}");
            text.AppendLine($"Сервер: {Fmt.E(server.Label)}");
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
                chatId, messageId, text.ToString().TrimEnd(),
                new InlineKeyboardMarkup(rows), ct);
        }
        catch (PanelApiException ex)
        {
            await HandlePanelErrorAsync(chatId, messageId, ex, $"rooms:{page}", ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chatId, messageId, ex, $"rooms:{page}", ct);
        }
    }

    private async Task ShowRoomAsync(
        long chatId, int? messageId, int number, CancellationToken ct)
    {
        try
        {
            var binding = await RequireBindingAsync(chatId, messageId, ct);
            if (binding is null)
                return;

            var room = await PanelFor(binding).GetRoomAsync(number, ct);
            var mine = room.Owner.Id == binding.PlayerId;

            var text = new StringBuilder();
            text.AppendLine($"🎮 <b>Комната #{room.Number}</b>");
            text.AppendLine($"Сервер: {Fmt.E(ProfileOf(binding)?.Label ?? binding.ServerId)}");
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
                chatId, messageId, text.ToString().TrimEnd(),
                new InlineKeyboardMarkup(rows), ct);
        }
        catch (PanelApiException ex)
        {
            await HandlePanelErrorAsync(chatId, messageId, ex, $"room:{number}", ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chatId, messageId, ex, $"room:{number}", ct);
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
        long chatId, int? messageId, int number, CancellationToken ct)
    {
        var text =
            $"Завершить комнату <b>#{number}</b>?\n\n" +
            "Катка закончится, игроки вернутся в лобби.";

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { Btn("Да, завершить", $"roomend:{number}:yes") },
            new[] { Btn("Нет", $"room:{number}") },
        });

        await EditOrSendAsync(chatId, messageId, text, keyboard, ct);
    }

    private async Task EndRoomAsync(
        long chatId, int? messageId, int number, CancellationToken ct)
    {
        var binding = await RequireBindingAsync(chatId, messageId, ct);
        if (binding is null)
            return;

        try
        {
            await PanelFor(binding).EndRoomAsync(binding.SessionToken, number, ct);
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
            await EditOrSendAsync(chatId, messageId, text, keyboard, ct);
        }
        catch (PanelApiException ex)
        {
            await HandlePanelErrorAsync(chatId, messageId, ex, $"room:{number}", ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chatId, messageId, ex, $"room:{number}", ct);
        }
    }

    // ── создание комнаты ────────────────────────────────────────────────────

    private async Task ShowCreateModesAsync(
        long chatId, int? messageId, CancellationToken ct)
    {
        var binding = await RequireBindingAsync(chatId, messageId, ct);
        if (binding is null)
            return;

        try
        {
            var modes = (await PanelFor(binding).GetModesAsync(ct)).Modes
                .Where(mode => mode.Implemented)
                .ToList();

            if (modes.Count == 0)
            {
                await EditOrSendAsync(
                    chatId, messageId,
                    "Сервер не отдал ни одного режима. Попробуй позже.",
                    RetryKeyboard("create"), ct);
                return;
            }

            State(chatId).Wizard = new CreateRoomWizard
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
                chatId, messageId,
                "➕ <b>Новая комната</b>\n\nВыбери режим. Показаны только те, что реально запускаются.",
                new InlineKeyboardMarkup(rows), ct);
        }
        catch (PanelApiException ex)
        {
            await HandlePanelErrorAsync(chatId, messageId, ex, "create", ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chatId, messageId, ex, "create", ct);
        }
    }

    private async Task ShowCreateMapsAsync(
        long chatId, int? messageId, string modeId, CancellationToken ct)
    {
        var binding = await RequireBindingAsync(chatId, messageId, ct);
        if (binding is null)
            return;

        try
        {
            var state = State(chatId);
            var wizard = state.Wizard as CreateRoomWizard;
            GameMode? mode = wizard?.Mode;

            if (mode is null || !mode.Id.Equals(modeId, StringComparison.Ordinal))
            {
                var modes = (await PanelFor(binding).GetModesAsync(ct)).Modes;
                mode = modes.FirstOrDefault(m => m.Id.Equals(modeId, StringComparison.Ordinal));
                if (mode is null)
                {
                    await ShowCreateModesAsync(chatId, messageId, ct);
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
                chatId, messageId,
                $"➕ <b>Новая комната</b>\n\nРежим: <b>{Fmt.E(mode.Name)}</b>\nВыбери карту.",
                new InlineKeyboardMarkup(rows), ct);
        }
        catch (PanelApiException ex)
        {
            await HandlePanelErrorAsync(chatId, messageId, ex, "create", ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chatId, messageId, ex, "create", ct);
        }
    }

    private async Task ShowCreateTeamsAsync(
        long chatId, int? messageId, string modeId, int levelIndex, CancellationToken ct)
    {
        var state = State(chatId);
        var wizard = state.Wizard as CreateRoomWizard;
        var mode = wizard?.Mode;
        if (wizard is null || mode is null
            || !mode.Id.Equals(modeId, StringComparison.Ordinal)
            || levelIndex < 0 || levelIndex >= mode.Levels.Count)
        {
            await ShowCreateMapsAsync(chatId, messageId, modeId, ct);
            return;
        }

        wizard.Level = mode.Levels[levelIndex];

        if (mode.TeamSizes.Count <= 1)
        {
            wizard.PerTeam = mode.TeamSizes.FirstOrDefault(mode.PerTeam);
            await ShowCreateNameAsync(chatId, messageId, ct);
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
            chatId, messageId,
            $"➕ <b>Новая комната</b>\n\n" +
            $"Режим: <b>{Fmt.E(mode.Name)}</b>\n" +
            $"Карта: <b>{Fmt.E(wizard.Level)}</b>\n\n" +
            "Сколько игроков в команде?",
            new InlineKeyboardMarkup(rows), ct);
    }

    private async Task ShowCreateNameAsync(
        long chatId, int? messageId, CancellationToken ct)
    {
        var state = State(chatId);
        if (state.Wizard is not CreateRoomWizard current
            || current.Mode is null
            || current.Level is null)
        {
            await ShowCreateModesAsync(chatId, messageId, ct);
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
            chatId, messageId,
            $"➕ <b>Новая комната</b>\n\n" +
            $"Режим: <b>{Fmt.E(mode.Name)}</b>\n" +
            $"Карта: <b>{Fmt.E(level)}</b>\n\n" +
            "Пришли название комнаты (до 32 символов) или нажми «Без названия».",
            new InlineKeyboardMarkup(rows), ct);
    }

    private async Task CreateRoomAsync(
        long chatId, int? messageId, CreateRoomWizard wizard, string rawName,
        CancellationToken ct)
    {
        var binding = await RequireBindingAsync(chatId, messageId, ct);
        if (binding is null)
            return;

        if (wizard.Mode is null || wizard.Level is null)
        {
            await ShowCreateModesAsync(chatId, messageId, ct);
            return;
        }

        var name = rawName.Trim();
        if (name.Length > 32)
            name = name[..32];

        try
        {
            var created = await PanelFor(binding).CreateRoomAsync(
                binding.SessionToken,
                new CreateRoomRequest
                {
                    Mode = wizard.Mode.Id,
                    Level = wizard.Level,
                    Name = name,
                    PerTeam = wizard.PerTeam,
                },
                ct);

            State(chatId).Wizard = null;

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

            await EditOrSendAsync(chatId, messageId, text, keyboard, ct);
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

            await EditOrSendAsync(chatId, messageId, text, keyboard, ct);
        }
        catch (Exception ex)
        {
            await NetworkErrorAsync(chatId, messageId, ex, "create", ct);
        }
    }

    // ── выбор сервера ───────────────────────────────────────────────────────

    /// <summary>
    /// Привязка человека к серверу, который он выбрал кодом из лобби. Сервер мог
    /// исчезнуть из <c>.env</c> — тогда привязка не считается рабочей.
    /// </summary>
    private UserBinding? ActiveBinding(long chatId)
    {
        var binding = _store.GetActive(chatId);
        if (binding is null)
            return null;
        return _config.FindServer(binding.ServerId) is null ? null : binding;
    }

    private ServerProfile? ProfileOf(UserBinding binding) =>
        _config.FindServer(binding.ServerId);

    /// <summary>Сервер привязки, а до входа — первый настроенный (Новосибирск).</summary>
    private ServerProfile SelectedServer(long chatId)
    {
        var binding = ActiveBinding(chatId);
        if (binding is not null && ProfileOf(binding) is { } bound)
            return bound;
        return _config.Servers[0];
    }

    private PanelClient PanelOf(ServerProfile server) =>
        _panels.TryGetValue(server.Id, out var panel)
            ? panel
            : throw new InvalidOperationException(
                $"нет клиента панели для сервера {server.Id}");

    private PanelClient PanelFor(UserBinding binding) =>
        _panels.TryGetValue(binding.ServerId, out var panel)
            ? panel
            : throw new InvalidOperationException(
                $"нет клиента панели для сервера {binding.ServerId}");

    // ── общие ответы ────────────────────────────────────────────────────────

    private async Task<UserBinding?> RequireBindingAsync(
        long chatId, int? messageId, CancellationToken ct)
    {
        var binding = ActiveBinding(chatId);
        if (binding is not null && !string.IsNullOrWhiteSpace(binding.SessionToken))
            return binding;

        await ShowAuthPromptAsync(chatId, messageId, ct);
        return null;
    }

    private async Task HandlePanelErrorAsync(
        long chatId, int? messageId, PanelApiException ex,
        string? retryData, CancellationToken ct)
    {
        var binding = ActiveBinding(chatId);
        if (ex.IsAuthLost)
        {
            if (binding is not null)
                _store.Delete(chatId, binding.ServerId);
            await ShowAuthPromptAsync(
                chatId, messageId, ct,
                "🔒 Сессия истекла. Зайди в лобби и пришли новый код из 6 цифр.");
            return;
        }

        var server = SelectedServer(chatId);
        var text = ex.Code switch
        {
            "bad_bot_key" or "bot_disabled" or "bot_key_unavailable" =>
                $"Сервер «{Fmt.E(server.Label)}» не принял ключ бота.\n" +
                $"Владельцу: проверь <code>{Fmt.E(server.KeyEnvName)}</code> в " +
                "<code>.env</code>; новый выдаёт <code>tgbot resetkey</code>.",
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
        await EditOrSendAsync(chatId, messageId, text, keyboard, ct);
    }

    private async Task NetworkErrorAsync(
        long chatId, int? messageId, Exception ex,
        string? retryData, CancellationToken ct)
    {
        var server = SelectedServer(chatId);
        Log($"сеть {server.Id}: {ex.Message}");
        var text =
            $"Сервер «{Fmt.E(server.Label)}» не отвечает.\n" +
            "Проверь, что игра запущена, и попробуй ещё раз.";
        var keyboard = retryData is null ? MenuKeyboard() : RetryKeyboard(retryData);
        await EditOrSendAsync(chatId, messageId, text, keyboard, ct);
    }

    private async Task SendAsync(
        long chatId, string text, InlineKeyboardMarkup? keyboard, CancellationToken ct)
    {
        await _bot.SendMessage(
            chatId, text,
            parseMode: ParseMode.Html,
            replyMarkup: keyboard,
            cancellationToken: ct);
    }

    private async Task EditOrSendAsync(
        long chatId, int? messageId, string text,
        InlineKeyboardMarkup? keyboard, CancellationToken ct)
    {
        if (messageId is { } id)
        {
            try
            {
                await _bot.EditMessageText(
                    chatId, id, text,
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

        await SendAsync(chatId, text, keyboard, ct);
    }

    private UserState State(long chatId) =>
        _states.GetOrAdd(chatId, _ => new UserState());

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
