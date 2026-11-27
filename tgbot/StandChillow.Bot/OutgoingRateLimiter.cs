namespace StandChillow.Bot;

/// <summary>
/// Ограничитель исходящих вызовов Telegram: не чаще раза в секунду в один чат,
/// не больше 20 сообщений в минуту в группу и не больше 25 в секунду на бота.
/// </summary>
/// <remarks>
/// <para>
/// Считаются и отправки, и правки сообщений: Telegram учитывает их в одном лимите.
/// Часы и задержка инжектируются, поэтому правила проверяются тестами без реального
/// сна. Лимиты чуть ниже телеграмовских (30/с и 20/мин) — запас на дрожание часов.
/// </para>
/// <para>
/// Ожидание не держит общий замок: пока один чат ждёт, другие могут отправлять.
/// </para>
/// </remarks>
public sealed class OutgoingRateLimiter
{
    private sealed class ChatState
    {
        public DateTime LastSentUtc;
        public readonly Queue<DateTime> SentUtc = new();
    }

    private static readonly TimeSpan Margin = TimeSpan.FromMilliseconds(50);

    private readonly object _gate = new();
    private readonly Dictionary<long, ChatState> _chats = new();
    private readonly Queue<DateTime> _global = new();
    private readonly Func<DateTime> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _perChat;
    private readonly int _groupLimit;
    private readonly TimeSpan _groupWindow;
    private readonly int _globalLimit;
    private readonly TimeSpan _globalWindow;

    public OutgoingRateLimiter(
        Func<DateTime>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? perChat = null,
        int groupLimit = 20,
        TimeSpan? groupWindow = null,
        int globalLimit = 25,
        TimeSpan? globalWindow = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
        _perChat = perChat ?? TimeSpan.FromSeconds(1);
        _groupLimit = Math.Max(1, groupLimit);
        _groupWindow = groupWindow ?? TimeSpan.FromSeconds(60);
        _globalLimit = Math.Max(1, globalLimit);
        _globalWindow = globalWindow ?? TimeSpan.FromSeconds(1);
    }

    public async Task WaitAsync(long chatId, bool isGroup, CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                var now = _clock();
                if (!_chats.TryGetValue(chatId, out var state))
                {
                    state = new ChatState();
                    _chats[chatId] = state;
                }

                Prune(state, now);
                PruneGlobal(now);

                wait = ComputeWait(state, isGroup, now);
                if (wait <= TimeSpan.Zero)
                {
                    state.LastSentUtc = now;
                    state.SentUtc.Enqueue(now);
                    _global.Enqueue(now);
                    return;
                }
            }

            await _delay(wait, ct);
        }
    }

    private TimeSpan ComputeWait(ChatState state, bool isGroup, DateTime now)
    {
        var wait = TimeSpan.Zero;

        if (state.LastSentUtc != default)
        {
            var next = state.LastSentUtc + _perChat;
            if (next > now)
                wait = next - now;
        }

        if (isGroup && state.SentUtc.Count >= _groupLimit)
        {
            var next = state.SentUtc.Peek() + _groupWindow;
            if (next > now)
                wait = Max(wait, next - now);
        }

        if (_global.Count >= _globalLimit)
        {
            var next = _global.Peek() + _globalWindow;
            if (next > now)
                wait = Max(wait, next - now);
        }

        return wait > TimeSpan.Zero ? wait + Margin : TimeSpan.Zero;
    }

    private void Prune(ChatState state, DateTime now)
    {
        while (state.SentUtc.Count > 0 && now - state.SentUtc.Peek() >= _groupWindow)
            state.SentUtc.Dequeue();
    }

    private void PruneGlobal(DateTime now)
    {
        while (_global.Count > 0 && now - _global.Peek() >= _globalWindow)
            _global.Dequeue();
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;
}
