using StandChillow.Bot;
using StandChillow.Bot.Contracts;

try
{
    var config = BotConfig.Load();

    using var store = new BotStore(config.DbPath);
    store.EnsureCreated();

    using var panel = new PanelClient(new PanelClientOptions
    {
        BaseUrl = new Uri(config.ApiBaseUrl),
        BotKey = config.BotKey,
        Timeout = config.ApiTimeout,
    });

    var bot = new TelegramBot(config, store, panel);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, args) =>
    {
        args.Cancel = true;
        cts.Cancel();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

    await bot.RunAsync(cts.Token);
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
