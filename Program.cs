using ArkRoxBot.Models;
using ArkRoxBot.Services;

HtmlClassifiedsScraper scraper = new HtmlClassifiedsScraper();

string html = await scraper.FetchClassifiedsPageAsync("Team Captain", 1);

if (!string.IsNullOrEmpty(html))
{
    Console.WriteLine("Successfully downloaded classifieds page!");
}
else
{
    Console.WriteLine("Failed to download classifieds page.");
}

Console.WriteLine("Press ENTER to exit...");
Console.ReadLine();