using ArkRoxBot.Services;

PlaywrightScraper scraper = new PlaywrightScraper();
await scraper.InitAsync();

string html = await scraper.FetchClassifiedsPageAsync("Team Captain", 1);

if (!string.IsNullOrEmpty(html))
{
    Console.WriteLine("Successfully fetched classifieds page with Playwright!");
}
else
{
    Console.WriteLine("Failed to fetch classifieds page.");
}

Console.WriteLine("Press ENTER to exit...");
Console.ReadLine();
