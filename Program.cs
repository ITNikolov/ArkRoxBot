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
        string username = Console.ReadLine() ?? string.Empty;         // username can be visible
        Console.Write("Steam password: ");
        string password = ReadMasked("Steam password: ");             // masked
        Console.Write("Steam 2FA: ");
        string twoFactor = ReadMasked("Steam 2FA (if any): ");        // masked


        // Start Steam (don’t await; it runs the callback pump)
        string apiKey = "A6FEBC05BEAD8EAC88F2439A5E8B8741";
        string botId64 = "76561199466477276"; // your bot's SteamID64

        TradeService trades = new TradeService(
            host.Services.GetRequiredService<PriceStore>(),
            host.Services.GetRequiredService<ItemConfigLoader>(),
            apiKey,
            botId64
        );
        trades.Start();

        // Run your scrape → price → post pipeline once
        await bot.RunAsync();

        Console.WriteLine("Bot running. Press ENTER to exit...");
        Console.ReadLine();
    }

    private static string ReadMasked(string prompt)
    {
        Console.Write(prompt);
        var sb = new System.Text.StringBuilder();
        ConsoleKeyInfo key;
        while (true)
        {
            key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); }
                continue;
            }
            if (!char.IsControl(key.KeyChar)) { sb.Append(key.KeyChar); Console.Write("*"); }
        }
        return sb.ToString();
    }
}

