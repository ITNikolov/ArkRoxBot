using ArkRoxBot.Services;

PlaywrightScraper scraper = new PlaywrightScraper();


await scraper.InitAsync();
await scraper.UpdateKeyPriceAsync();
await scraper.FetchAllPagesAsync("Team Captain");

Console.WriteLine("Press ENTER to exit...");
Console.ReadLine();
