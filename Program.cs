using ArkRoxBot.Interfaces;
using ArkRoxBot.Models;
using ArkRoxBot.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        using IHost host = Host.CreateDefaultBuilder(args)
            .ConfigureServices(services =>
            {
                services.AddSingleton<CommandService>();
                services.AddSingleton<IKeyPriceTracker, KeyPriceTracker>();
                services.AddSingleton<IPriceParser, PriceParser>();
                services.AddSingleton<PriceCalculator>();
                services.AddSingleton<PlaywrightScraper>();
                services.AddSingleton<PriceStore>();
                services.AddSingleton<BackpackListingService>();
                services.AddSingleton<ItemConfigLoader>(_ => new ItemConfigLoader("Data/items.json"));

                services.AddSingleton<ISteamClientService, SteamClientService>();

                services.AddSingleton<OfferEvaluator>(sp =>
                {
                    var store = sp.GetRequiredService<PriceStore>();
                    const decimal minProfitRef = 0.11m;
                    return new OfferEvaluator(store, minProfitRef);
                });

                services.AddSingleton<TradeService>(sp =>
                {
                    var store = sp.GetRequiredService<PriceStore>();
                    var items = sp.GetRequiredService<ItemConfigLoader>();
                    var evaluator = sp.GetRequiredService<OfferEvaluator>();

                    string apiKey = "A6FEBC05BEAD8EAC88F2439A5E8B8741";
                    string botId = "76561199466477276";

                    return new TradeService(store, items, apiKey, botId, evaluator);
                });

                services.AddSingleton<InventoryService>(sp =>
                {
                    var botId = "76561199466477276";
                    return new InventoryService(botId);
                });

                services.AddSingleton<BotService>();       // ok even if no Stop()

                services.AddHostedService<BotApp>();
            })
            .Build();

        var sp = host.Services;
        var lifetime = sp.GetRequiredService<IHostApplicationLifetime>();

        lifetime.ApplicationStopping.Register(() =>
        {
            try { sp.GetRequiredService<TradeService>().Stop(); } catch { }
            try
            {
                sp.GetRequiredService<ISteamClientService>()
                .StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch { }
        });

        AppDomain.CurrentDomain.ProcessExit += (_, __) =>
        {
            try
            {
                sp.GetRequiredService<ISteamClientService>()
                .StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch { }
        };

        await host.RunAsync();
    }
}
