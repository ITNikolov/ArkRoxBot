using ArkRoxBot.CommandSystem;
using ArkRoxBot.Interfaces;
using ArkRoxBot.Models;
using ArkRoxBot.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton<CommandService>();
        services.AddSingleton<IKeyPriceTracker, KeyPriceTracker>();
        services.AddSingleton<IPriceParser, PriceParser>();
        services.AddSingleton<PriceCalculator>();
        services.AddSingleton<PlaywrightScraper>();
        services.AddSingleton<PriceStore>();
        services.AddSingleton<BackpackListingService>();
        services.AddSingleton<ItemConfigLoader>(provider =>
    new ItemConfigLoader("Data/items.json"));
        services.AddSingleton<BotService>(); // Main bot runner
    })
    .Build();

await host.Services.GetRequiredService<BotService>().RunAsync();

Console.WriteLine("Press ENTER to exit...");
Console.ReadLine();
// sell orderite imat li nujda ot kut 10 % on top ? pri polojenie che tam se namirat nai evtinnite listingi i te pravi malko ne konkurentno sposoben sprqmo drugite bottove 10 % bottom trim e okay 