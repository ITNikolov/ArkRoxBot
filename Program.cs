using System;
using System.Threading.Tasks;
using ArkRoxBot.CommandSystem;
using ArkRoxBot.Interfaces;
using ArkRoxBot.Models;
using ArkRoxBot.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        IHost host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((HostBuilderContext context, IServiceCollection services) =>
            {
                services.AddSingleton<CommandService>();
                services.AddSingleton<IKeyPriceTracker, KeyPriceTracker>();
                services.AddSingleton<IPriceParser, PriceParser>();
                services.AddSingleton<PriceCalculator>();
                services.AddSingleton<PlaywrightScraper>();
                services.AddSingleton<PriceStore>();
                services.AddSingleton<BackpackListingService>();
                services.AddSingleton<ItemConfigLoader>(sp => new ItemConfigLoader("Data/items.json"));

                // Steam + orchestrator
                services.AddSingleton<ISteamClientService, SteamClientService>();
                services.AddSingleton<BotService>();
            })
            .Build();

        ISteamClientService steam = host.Services.GetRequiredService<ISteamClientService>();
        BotService bot = host.Services.GetRequiredService<BotService>();

        Console.Write("Steam username: ");
        string username = Console.ReadLine() ?? string.Empty;

        Console.Write("Steam password: ");
        string password = Console.ReadLine() ?? string.Empty;

        Console.Write("Steam 2FA (or empty): ");
        string twoFactor = Console.ReadLine() ?? string.Empty;

        // Start Steam (don’t await; it runs the callback pump)
        Task connectTask = steam.ConnectAndLoginAsync(username, password, twoFactor);

        // Run your scrape → price → post pipeline once
        await bot.RunAsync();

        Console.WriteLine("Bot running. Press ENTER to exit...");
        Console.ReadLine();
    }
}
