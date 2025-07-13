using Microsoft.Playwright;

namespace ArkRoxBot.Services
{
    public class PlaywrightScraper
    {
        private IBrowserContext? _context;
        private IPage? _sharedPage;

        private async Task ImportCookiesFromFileAsync(IBrowserContext context, string filePath)
        {
            var json = await File.ReadAllTextAsync(filePath);
            var rawCookies = System.Text.Json.JsonDocument.Parse(json).RootElement;
            var cookies = new List<Cookie>();

            foreach (var cookieJson in rawCookies.EnumerateArray())
            {
                cookies.Add(new Cookie
                {
                    Name = cookieJson.GetProperty("name").GetString(),
                    Value = cookieJson.GetProperty("value").GetString(),
                    Domain = cookieJson.GetProperty("domain").GetString(),
                    Path = cookieJson.TryGetProperty("path", out var p) ? p.GetString() : "/"
                });
            }

            await context.AddCookiesAsync(cookies);
            Console.WriteLine($"Imported {cookies.Count} cookies from {filePath}");
        }

        public async Task InitAsync()
        {
            var playwright = await Playwright.CreateAsync();

            _context = await playwright.Chromium.LaunchPersistentContextAsync(
                @"E:\\ArkRox\\arkrox-userdata",
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = false,
                    SlowMo = 50
                });

            await ImportCookiesFromFileAsync(_context, @"E:\\ArkRox\\arkrox-userdata\\bp_cookies.json");
            await ImportCookiesFromFileAsync(_context, @"E:\\ArkRox\\arkrox-userdata\\steam_cookies.json");

            await _context.AddCookiesAsync(new[]
            {
                new Cookie
                {
                    Name = "cf_clearance",
                    Value = "J9Mtt4bpH_YXd3ejlRaF2...", // truncated for readability
                    Domain = "backpack.tf",
                    Path = "/"
                }
            });

            _sharedPage = await _context.NewPageAsync();
        }

        public async Task<string> FetchClassifiedsPageAsync(string itemName, int page = 1)
        {
            if (_sharedPage == null)
            {
                Console.WriteLine("Error: Shared browser page is not initialized.");
                return string.Empty;
            }

            string urlName = Uri.EscapeDataString(itemName);
            string url = $"https://backpack.tf/classifieds?page={page}&item={urlName}&quality=6&tradable=1&craftable=1&australium=-1&killstreak_tier=0";

            await _sharedPage.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Task.Delay(3000);

            await _sharedPage.WaitForSelectorAsync("li.listing", new PageWaitForSelectorOptions
            {
                Timeout = 15000,
                State = WaitForSelectorState.Visible
            });

            await ExtractListingsFromPageAsync(_sharedPage);

            return await _sharedPage.ContentAsync();
        }

        public async Task FetchAllPagesAsync(string itemName)
        {
            for (int page = 1; page <= 2; page++)
            {
                string content = await FetchClassifiedsPageAsync(itemName, page);
                Console.WriteLine($"✅ Page {page} scraped for '{itemName}' | Length: {content.Length}");
                await Task.Delay(1000);
            }
        }

        private async Task ExtractListingsFromPageAsync(IPage page)
        {
            // Wait for page to load listings
            await page.WaitForSelectorAsync("div.listing", new() { Timeout = 8000 });

            var listings = await page.QuerySelectorAllAsync("div.listing");
            Console.WriteLine($">> Listings Found: {listings.Count}");

            foreach (var listing in listings)
            {
                string? price = await listing.GetAttributeAsync("data-listing_price");

                if (price == null)
                {
                    var children = await listing.QuerySelectorAllAsync("*");
                    foreach (var child in children)
                    {
                        price = await child.GetAttributeAsync("data-listing_price");
                        if (price != null) break;
                    }
                }

                Console.WriteLine($">> Price: {price ?? "N/A"} ref");
            }
        }


    }
}
