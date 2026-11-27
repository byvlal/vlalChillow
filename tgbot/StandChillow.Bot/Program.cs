using StandChillow.Bot;
using StandChillow.Bot.Contracts;

try
{
    var config = BotConfig.Load();

    using var store = new BotStore(config.DbPath);
    store.EnsureCreated();

    // По клиенту на сервер: у локального стенда и у Новосибирска свои ключи и адреса.
    // Какой из них обслуживать, решает код из лобби, а не режим в .env.
    var panels = new Dictionary<string, PanelClient>(StringComparer.Ordinal);
    try
    {
        foreach (var server in config.Servers)
        {
            panels[server.Id] = new PanelClient(new PanelClientOptions
            {
                BaseUrl = new Uri(server.ApiBaseUrl),
                BotKey = server.BotKey,
                Timeout = config.ApiTimeout,
            });
        }

        var bot = new TelegramBot(config, store, panels);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            cts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        await bot.RunAsync(cts.Token);
    }
    finally
    {
        foreach (var panel in panels.Values)
            panel.Dispose();
    }
}
catch (BotConfigException ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 2;
}
catch (OperationCanceledException)
{
    // Штатная остановка по SIGTERM/Ctrl+C.
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[bot] фатально: {ex}");
    Environment.ExitCode = 1;
}
