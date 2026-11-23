using System.Net;
using LiteNetLib;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Net.Lobby;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Ответы забаненному: полное уведомление на команду, повтор для теневого пира и
/// отложенный разрыв сокета. Вынесено из <c>GameNetHost.Bans.cs</c>, чтобы файл
/// с реестром и командами не разрастался выше общего лимита.
/// </summary>
public sealed partial class GameNetHost
{
    /// <summary>
    /// Команда от забаненного: ничего не выполняем, но повторяем полное уведомление
    /// (причина, кто забанил, ID, номер бана, срок). Возвращает true — команда
    /// обработана отказом.
    /// </summary>
    private bool RefuseBannedCommand(
        ConnectedPlayer player,
        string channel,
        IPAddress? sourceIp)
    {
        if (_bans is null || !_config.Admin.Ban.Enabled)
            return false;

        var address = sourceIp ?? player.Peer.Address;
        var decision = InspectBan(player.Name, address, player.PublicId, learn: false);
        if (!decision.Banned || decision.Record is null)
            return false;

        if (channel == "match")
        {
            // Уведомление и кик — по аккаунту, устройству и адресу, а не по нику:
            // тёзка в катке не должен получить чужой бан, а забаненный — остаться.
            var kicked = _match.DisconnectBanned(
                player.PublicId, address, decision.Record.Devices, decision.Record);
            if (kicked == 0)
            {
                // Сокет не нашёлся (например, успел переподключиться): команда всё
                // равно не выполняется, а уведомление уходит запасным путём.
                foreach (var line in BanMessages.NoticeLines(decision.Record, DateTime.UtcNow))
                    _match.SendServerChat(line, nick: player.Name, alsoLastChanceIp: address);
            }
        }
        else
        {
            SendBanNoticeToLobbyPeer(player.Peer, decision.Record);
            DisconnectSoon(player.Peer);
        }

        Console.WriteLine(
            $"[ban] команда забаненного: id=#{player.PublicId} nick='{player.Name}' " +
            $"channel={channel} — бан №{BanMessages.BanIdOf(decision.Record)}");
        return true;
    }

    /// <summary>
    /// Забаненный в теневом лобби написал команду: повторяем полное уведомление
    /// (с ID и номером бана), но не чаще <c>admin.ban.noticeCooldownSec</c>.
    /// </summary>
    private bool TrySendBanNotice(NetPeer peer)
    {
        if (_bans is null || !_config.Admin.Ban.Enabled)
            return false;

        string nick;
        lock (_shadowGate)
        {
            if (!_shadowPeers.TryGetValue(peer, out var shadowNick))
                return false;

            nick = shadowNick;
            var now = Environment.TickCount64;
            var cooldown = Math.Max(1, _config.Admin.Ban.NoticeCooldownSec) * 1000L;
            if (_shadowNoticeTicks.TryGetValue(peer, out var last) && now - last < cooldown)
                return true;
            _shadowNoticeTicks[peer] = now;
        }

        var decision = InspectBan(nick, peer.Address, learn: false);
        if (!decision.Banned || decision.Record is null)
            return false;

        SendBanNoticeToLobbyPeer(peer, decision.Record);
        return true;
    }

    private void SendBanNoticeToLobbyPeer(NetPeer peer, BanRecord record)
    {
        foreach (var line in BanMessages.NoticeLines(record, DateTime.UtcNow))
            SendServerChatTo(peer, line);
    }

    /// <summary>Разорвать сокет чуть позже: надёжное сообщение должно успеть уйти.</summary>
    private static void DisconnectSoon(NetPeer peer, int delayMs = 150)
    {
        _ = Task.Delay(Math.Max(0, delayMs)).ContinueWith(_ =>
        {
            try { peer.Disconnect(); }
            catch { /* уже мёртв */ }
        });
    }
}
