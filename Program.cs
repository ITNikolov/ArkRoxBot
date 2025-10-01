using System;
using System.Threading.Tasks;
using ArkRoxBot.Interfaces;
using ArkRoxBot.Models;
using ArkRoxBot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        using IHost host = Host.CreateDefaultBuilder(args)
            // If you keep appsettings.json in Data/, load it here.
            .ConfigureAppConfiguration((context, config) =>
            {
                config.AddJsonFile("Data/appsettings.json", optional: true, reloadOnChange: true);
            })
            .ConfigureServices((context, services) =>
            {
                // IConfiguration is already registered by the host; the next line is optional.
                services.AddSingleton<IConfiguration>(context.Configuration);

                // Core singletons
                services.AddSingleton<PriceStore>();
                services.AddSingleton<ItemConfigLoader>(_ => new ItemConfigLoader("Data/items.json"));

                services.AddSingleton<InventoryService>(_ =>
                    new InventoryService(context.Configuration["Steam:BotId"] ?? "YOUR_BOT_STEAMID64"));

                services.AddSingleton<IKeyPriceTracker, KeyPriceTracker>();
                services.AddSingleton<IPriceParser, PriceParser>();
                services.AddSingleton<PriceCalculator>();
                services.AddSingleton<PlaywrightScraper>();
                services.AddSingleton<BackpackListingService>();

                services.AddSingleton<OfferEvaluator>(sp =>
                {
                    PriceStore store = sp.GetRequiredService<PriceStore>();
                    const decimal minProfitRef = 0.11m;
                    return new OfferEvaluator(store, minProfitRef);
                });

                // Register concrete TradeService (since your CommandService and BotApp depend on TradeService)
                services.AddSingleton<TradeService>(sp =>
                {
                    IConfiguration cfg = sp.GetRequiredService<IConfiguration>();
                    PriceStore store = sp.GetRequiredService<PriceStore>();
                    ItemConfigLoader items = sp.GetRequiredService<ItemConfigLoader>();
                    OfferEvaluator evaluator = sp.GetRequiredService<OfferEvaluator>();

                    string apiKey = cfg["Steam:ApiKey"] ?? "YOUR_API_KEY";
                    string botId = cfg["Steam:BotId"] ?? "YOUR_BOT_STEAMID64";

                    return new TradeService(store, items, apiKey, botId, evaluator);
                });

                // CommandService needs TradeService and IConfiguration
                services.AddSingleton<CommandService>(sp =>
                    new CommandService(
                        sp.GetRequiredService<PriceStore>(),
                        sp.GetRequiredService<InventoryService>(),
                        sp.GetRequiredService<TradeService>(),
                        sp.GetRequiredService<IConfiguration>()));

                services.AddSingleton<ISteamClientService, SteamClientService>();
                services.AddSingleton<BotService>();
                services.AddHostedService<BotApp>();
            })
            .Build();

        // Graceful shutdown hooks
        IServiceProvider sp = host.Services;
        IHostApplicationLifetime lifetime = sp.GetRequiredService<IHostApplicationLifetime>();

        lifetime.ApplicationStopping.Register(() =>
        {
            try { sp.GetRequiredService<TradeService>().Stop(); } catch { }
            try
            {
                sp.GetRequiredService<ISteamClientService>()
                  .StopAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            }
            catch { }
        });

        AppDomain.CurrentDomain.ProcessExit += (_, __) =>
        {
            try
            {
                sp.GetRequiredService<ISteamClientService>()
                  .StopAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            }
            catch { }
        };

        await host.RunAsync();
    }
}
