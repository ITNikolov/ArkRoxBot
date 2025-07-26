using ArkRoxBot.Interfaces;
using ArkRoxBot.Models;
using ArkRoxBot.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton<IKeyPriceTracker, KeyPriceTracker>();
        services.AddSingleton<IPriceParser, PriceParser>();
        services.AddSingleton<PriceCalculator>();
        services.AddSingleton<PlaywrightScraper>();
        services.AddSingleton<PriceStore>();
        services.AddSingleton<BotService>(); // Main bot runner
    })
    .Build();

await host.Services.GetRequiredService<BotService>().RunAsync();

Console.WriteLine("Press ENTER to exit...");
Console.ReadLine();
