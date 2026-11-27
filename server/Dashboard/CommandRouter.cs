using System.Text;
using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Plugins;
using StandChillow.LanServer.Web;

namespace StandChillow.LanServer.Dashboard;

/// <summary>Dispatches console-only commands and delegates shared aliases to the phone-chat command path.</summary>
public sealed class CommandRouter
{
    private readonly GameNetHost _game;
    private readonly PluginManager _plugins;
    private readonly LanDiscoveryHost _discovery;
    private readonly Func<LanDiscoveryInfo> _getDiscoveryInfo;
    private readonly Action<LanDiscoveryInfo> _setDiscoveryInfo;
    private readonly Action<string> _setLobbyTitle;
    private readonly Action _syncDiscovery;
    private readonly Action<string> _feedback;
    private readonly BotKeyStore _botKey;

    public CommandRouter(
        GameNetHost game,
        PluginManager plugins,
        LanDiscoveryHost discovery,
        BotKeyStore botKey,
        Func<LanDiscoveryInfo> getDiscoveryInfo,
        Action<LanDiscoveryInfo> setDiscoveryInfo,
        Action<string> setLobbyTitle,
        Action syncDiscovery,
        Action<string> feedback)
    {
        _game = game;
        _plugins = plugins;
        _discovery = discovery;
        _getDiscoveryInfo = getDiscoveryInfo;
        _setDiscoveryInfo = setDiscoveryInfo;
        _setLobbyTitle = setLobbyTitle;
        _syncDiscovery = syncDiscovery;
        _feedback = feedback;
        _botKey = botKey;
    }

    public bool Handle(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return true;

        // Slash form: /set round, /ban, … — same as phone chat (silent command, no chat bubble).
        if (trimmed.StartsWith('/'))
        {
            var body = trimmed[1..].Trim();
            if (body.Length == 0) return true;
            return Handle(body);
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts[0].Length == 0) return true;
        var cmd0 = parts[0].ToLowerInvariant();

        // Shared host commands (mode/map/set/end/help) — same as in-game /.
        // Запуска катки тут больше нет: play/start убраны, комнаты стартуют сами.
        if (cmd0 is "mode" or "map" or "maps" or "level" or "levels" or "set"
            or "end" or "stopmatch" or "стопматч" or "help" or "?" or "команды"
            or "режим" or "карта" or "ban" or "бан" or "unban" or "разбан"
            or "bans" or "баны" or "ids" or "айди"
            or "auth" or "код" or "whoami" or "ктоя"
            or "admin" or "админ")
        {
            if (!_game.TryDispatchHostCommand(trimmed, _feedback, who: "console"))
                _feedback($"unknown command '{trimmed}' — help");
            return true;
        }

        switch (cmd0)
        {
            case "quit":
            case "exit":
                return false;
            case "stat":
                // Typo for status — never chat/teardown.
                goto case "status";
            case "title" when parts.Length == 2:
                _setLobbyTitle(parts[1]);
                _feedback($"title → {parts[1]}");
                break;
            case "members" when parts.Length == 2 && byte.TryParse(parts[1], out var n):
                var next = _getDiscoveryInfo() with { MemberCount = n };
                _setDiscoveryInfo(next);
                _discovery.UpdateInfo(next);
                break;
            case "binds":
                foreach (var e in _discovery.BoundEndpoints)
                    _feedback($"{e.InterfaceName} {e.Address} br={e.Broadcast}");
                break;
            case "roster":
                foreach (var (name, status) in _game.Session.SnapshotRoster())
                    _feedback($"{name} ({LobbySession.FormatStatus(status)})");
                break;
            case "config":
                if (parts.Length == 2 && parts[1].Equals("reload", StringComparison.OrdinalIgnoreCase))
                    _game.ReloadConfig(_feedback);
                else
                    _game.ShowConfig(_feedback);
                break;
            case "status":
                _syncDiscovery();
                _feedback(
                    $"members={_getDiscoveryInfo().MemberCount} mode={_game.Session.GameModeId} " +
                    $"match={_game.MatchStarted} peers={_game.PeerCount} joined={_game.JoinedCount} " +
                    $"matchPeers={_game.Match.PeerCount}");
                _feedback(MatchHostSettings.FormatStatusLine());
                break;
            case "plugins":
            case "plugin":
            {
                var sub = parts.Length > 1 ? parts[1].Trim() : "";
                if (sub.Equals("reload", StringComparison.OrdinalIgnoreCase))
                {
                    var n = _plugins.Reload();
                    _feedback($"[plugins] reloaded → {n} ({string.Join(", ", _plugins.LoadedNames)})");
                }
                else
                {
                    var names = _plugins.LoadedNames;
                    _feedback(
                        names.Count == 0
                            ? $"[plugins] none loaded (drop DLLs in {_plugins.PluginsRoot})"
                            : $"[plugins] loaded: {string.Join(", ", names)}");
                    _feedback("usage: plugins | plugins reload");
                }
                break;
            }
            case "tgbot":
                HandleTgbot(parts.Length > 1 ? parts[1].Trim() : "");
                break;
            default:
            {
                // Plugin console commands first; else free text → lobby chat as Server.
                var cmd = parts[0];
                var argsRest = parts.Length > 1 ? parts[1] : "";
                if (_plugins.TryHandleConsoleCommand(cmd, argsRest, _feedback))
                    break;
                _game.BroadcastConsoleChat(trimmed);
                break;
            }
        }
        return true;
    }

    /// <summary>
    /// Консольные команды бота. Сброс ключа печатает сам ключ только в терминал
    /// (<c>/dev/tty</c>), мимо дашборда и <c>latest.log</c>: ключ — секрет, а лог
    /// переживает сессию и уезжает в бэкапы.
    /// </summary>
    private void HandleTgbot(string sub)
    {
        if (!sub.Equals("resetkey", StringComparison.OrdinalIgnoreCase)
            && !sub.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            _feedback("usage: tgbot resetkey — новый ключ бота (старый сразу перестанет работать)");
            return;
        }

        var result = _botKey.Rotate();
        if (!result.Ok)
        {
            _feedback($"[tgbot] не смог записать ключ: {result.Error}");
            return;
        }

        _feedback($"[tgbot] ключ сброшен: {result.Path}");
        var secret =
            $"\n=== новый ключ бота (старый больше не работает) ===\n{result.Key}\n" +
            "=== впиши его в REMOTE_BOT_KEY (или LOCAL_BOT_KEY) и перезапусти бота ===\n";
        if (!TryWriteSecretToTty(secret))
            _feedback("[tgbot] терминала нет — ключ лежит в файле выше");
    }

    /// <summary>
    /// Записать секрет прямо в управляющий терминал. При запуске из systemd без TTY
    /// честно возвращаем <c>false</c>: пусть человек возьмёт ключ из файла.
    /// </summary>
    private static bool TryWriteSecretToTty(string text)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                Console.Out.Write(text);
                Console.Out.Flush();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        try
        {
            using var tty = new FileStream(
                "/dev/tty", FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(tty, new UTF8Encoding(false)) { AutoFlush = true };
            writer.Write(text);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
