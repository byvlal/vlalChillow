namespace StandChillow.LanServer.Stats;

/// <summary>
/// Один игрок в записи истории матча — то, что показывают итоги катки.
/// </summary>
/// <remarks>
/// <para>
/// Значения приходят из тех же мест, что и таблица дашборда: убийства/помощи/счёт/деньги/пинг
/// читаются из actor props, смерти считает сам сервер. Ничего не выдумываем: чего клиент
/// не прислал, то и остаётся нулём.
/// </para>
/// <para>
/// <see cref="ActorNr"/> нужен только при сборе записи (по нему сшиваются раунды и смерти) —
/// в API он не отдаётся.
/// </para>
/// </remarks>
public sealed class MatchHistoryPlayer
{
    /// <summary>Серверный id игрока — постоянный номер аккаунта (<see cref="AccountStore.FirstId"/>).</summary>
    public int Id { get; set; }
    public string Nick { get; set; } = "";
    /// <summary><c>CT</c>, <c>T</c>, <c>SPEC</c> или <c>NONE</c>.</summary>
    public string Team { get; set; } = "NONE";
    public int Kills { get; set; }
    public int Assists { get; set; }
    public int Deaths { get; set; }
    public int Score { get; set; }
    public int Money { get; set; }
    public int Ping { get; set; } = -1;
    /// <summary>Игрок был в последних раундах, но до конца катки не остался.</summary>
    public bool Left { get; set; }
    /// <summary>Номер актёра в комнате — внутренняя кухня сбора.</summary>
    public int ActorNr { get; set; }
}

/// <summary>
/// Запись о сыгранной катке: когда, что, с каким счётом и кто в ней был.
/// </summary>
/// <remarks>
/// Пишется один раз на забег комнаты — в момент финала, «Завершить игру» или очистки
/// комнаты. Катка, которая не началась (ни одного раунда), записи не получает.
/// </remarks>
public sealed class MatchHistoryRecord
{
    public int Id { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime EndedUtc { get; set; }
    /// <summary>Каталожный id режима (<c>Ranked2v2</c>, <c>Duel</c>, …).</summary>
    public string ModeId { get; set; } = "";
    public string Map { get; set; } = "";
    public int ScoreCt { get; set; }
    public int ScoreTr { get; set; }
    /// <summary><c>CT</c>, <c>T</c> или <c>DRAW</c>.</summary>
    public string Winner { get; set; } = "DRAW";
    public int Rounds { get; set; }
    public int RoomNumber { get; set; }
    public string RoomName { get; set; } = "";
    public List<MatchHistoryPlayer> Players { get; set; } = new();

    /// <summary>Сводка для списка истории: строка этого игрока в катке.</summary>
    public MatchHistoryPlayer? Player(int id)
    {
        foreach (var p in Players)
        {
            if (p.Id == id)
                return p;
        }
        return null;
    }
}
