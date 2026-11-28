using LiteNetLib;
using StandChillow.LanServer.Dashboard;
using StandChillow.LanServer.Net.Lobby;

namespace StandChillow.LanServer.Net;

public sealed partial class GameNetHost
{
    /// <summary>
    /// Пиры, которых пустили в скрытое лобби только чтобы объяснить, почему ник не годится.
    /// </summary>
    /// <remarks>
    /// Отдельно от <c>_shadowPeers</c> (баны): там раз в минуту проверяется срок бана, и
    /// «бан снят — заходи заново» такому пиру было бы враньём. Здесь только ник и таймер.
    /// </remarks>
    private readonly Dictionary<NetPeer, string> _nickRejectedPeers = new();
    private readonly object _nickRejectGate = new();

    /// <summary>
    /// Ник не по правилам: пускаем в скрытое лобби (как забаненного), показываем подсказку
    /// и кикаем через <c>names.kickDelaySec</c>, если человек сам не вышел сменить ник.
    /// </summary>
    /// <remarks>
    /// Обычный отказ <c>ProfileValidationFailed</c> клиент показывает молча (или никак), и
    /// со стороны это выглядит как «сервер не пускает». Скрытый вход даёт ровно тот же
    /// экран, что у забаненного, — там чат работает, и подсказку видно.
    /// </remarks>
    private void HandleNickReject(NetPeer peer, JoinLobbyRequest req)
    {
        var result = _session.HandleShadowJoinRequest(peer, req);
        if (result.ResponseBytes is not null)
            Send(peer, result.ResponseBytes);

        if (!result.Success)
        {
            Console.WriteLine(
                $"[nick] '{NickPolicy.Describe(req.Profile.Name)}' отклонён, " +
                $"и скрытый вход не удался: {result.FailReason} ({result.Detail})");
            return;
        }

        var check = CheckNickForJoin(req.Profile.Name);
        var suggest = NickPolicy.Suggest(req.Profile.Name, _config.Names.MaxLength);
        var shown = NickPolicy.Describe(req.Profile.Name);

        lock (_nickRejectGate)
            _nickRejectedPeers[peer] = shown;

        Console.WriteLine(
            $"[nick] '{shown}' ({peer.Address}) — {check.Reason}; " +
            $"подсказка '{(suggest.Length > 0 ? suggest : "нет")}', " +
            $"кик через {_config.Names.KickDelaySec} с");

        DashboardHub.PostChat(
            LobbySession.ServerPlayerName,
            $"[ник] {shown} ({peer.Address}) — {check.Reason}",
            fromServer: true);

        // Три строки по порядку: ReliableOrdered доставит их так же, как приветствие.
        // Старый ник показываем в квадратных скобках, а не в угловых: угловые клиент
        // нарисовал бы как разметку, и сообщение сломалось бы само о себя.
        if (check.Impersonation)
        {
            // Подсказка «смени одну букву» здесь вредна: нужен полностью другой ник.
            SendServerChatTo(peer, $"ник «{shown}» выдаёт себя за влала");
            SendServerChatTo(peer, "возьми полностью другой ник — этот не подойдёт");
        }
        else
        {
            SendServerChatTo(peer, $"ник хуйня ебаная: «{shown}»");
            SendServerChatTo(peer, suggest.Length > 0
                ? $"иди смени на «{suggest}» — буквы, цифры, пробел, _ и -"
                : "иди смени ник — нужны буквы или цифры, пробел, _ и -");
        }
        SendServerChatTo(peer, $"кик через {_config.Names.KickDelaySec} сек");

        var delayMs = Math.Max(1, _config.Names.KickDelaySec) * 1000;
        Schedule(delayMs, () =>
        {
            // Человек мог выйти сам или уже перезайти с чистым ником — тогда не трогаем.
            if (!IsNickRejected(peer))
                return;

            try { peer.Disconnect(); }
            catch { /* уже мёртв */ }

            Console.WriteLine(
                $"[nick] '{shown}' не сменил ник за {_config.Names.KickDelaySec} с — кик");
        });
    }

    private bool IsNickRejected(NetPeer peer)
    {
        lock (_nickRejectGate)
            return _nickRejectedPeers.ContainsKey(peer);
    }

    /// <summary>
    /// Можно ли пустить ник в лобби и катку: обычные правила плюс анти-Влал.
    /// Админам и мега-админам анти-Влал не применяется — права даёт конфиг, не ник.
    /// </summary>
    private bool IsNickAllowedForJoin(string? nick) =>
        CheckNickForJoin(nick).Ok;

    /// <summary>Та же проверка с причиной отказа — для подсказки в скрытом лобби.</summary>
    private NickCheck CheckNickForJoin(string? nick) =>
        NickPolicy.CheckForJoin(
            nick,
            _config.Names.MaxLength,
            allowVlal: IsAdminId(IdOf(nick)));

    private void ForgetNickReject(NetPeer peer)
    {
        lock (_nickRejectGate)
            _nickRejectedPeers.Remove(peer);
    }
}
