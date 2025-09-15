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
                services.AddSingleton<TradeService>(sp =>
                {
                    var store = sp.GetRequiredService<PriceStore>();
                    var items = sp.GetRequiredService<ItemConfigLoader>();

                    var apiKey = "A6FEBC05BEAD8EAC88F2439A5E8B8741";
                    var botId = "76561199466477276";

                    return new TradeService(store, items, apiKey, botId);
                });
                services.AddSingleton<InventoryService>(sp =>
                {
                    var botId = "76561199466477276"; // same as TradeService
                    return new InventoryService(botId);
                });
                services.AddSingleton<BotService>();

                services.AddHostedService<BotApp>();

            })
            .Build();

        await host.RunAsync(); 
    }
}
