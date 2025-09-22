using ArkRoxBot.Interfaces;
using ArkRoxBot.Models;
using Microsoft.Playwright;

namespace ArkRoxBot.Services
{
    public class PlaywrightScraper
    {
        private IBrowserContext? _context;
        private IPage? _sharedPage;
        private readonly PriceCalculator _priceCalculator;
        private readonly IKeyPriceTracker _keyPriceTracker;

        public PlaywrightScraper(PriceCalculator priceCalculator, IKeyPriceTracker keyPriceTracker)
        {
            _priceCalculator = priceCalculator;
            _keyPriceTracker = keyPriceTracker;
        }



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
                    Value = "J9Mtt4bpH_YXd3ejlRaF2...", 
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

            // --- normalize & encode the item name ---
            string item = (itemName ?? string.Empty).Trim();          // kill stray spaces
            item = StripLeadingThe(item);                              // "The Team Captain" -> "Team Captain"
                                                                       // Optional: collapse internal double spaces if you want:
                                                                       // item = Regex.Replace(item, @"\s+", " ");

            string itemParam = Uri.EscapeDataString(item);

            string url = $"https://backpack.tf/classifieds?page={page}" +
                         $"&item={itemParam}" +
                         $"&quality=6&tradable=1&craftable=1&australium=-1&killstreak_tier=0";

            Console.WriteLine("[BP] URL -> " + url);

            // Navigate
            await _sharedPage.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 45000
            });

            // Wait for either listings or the empty state; backpack.tf is dynamic
            var listingsTask = _sharedPage.WaitForSelectorAsync("li.listing", new()
            {
                Timeout = 12000,
                State = WaitForSelectorState.Attached
            });
            var emptyTask = _sharedPage.WaitForSelectorAsync(":has-text('No items found')", new()
            {
                Timeout = 12000,
                State = WaitForSelectorState.Attached
            });

            try { await Task.WhenAny(listingsTask, emptyTask); }
            catch { /* ignore; we'll return the HTML anyway */ }

            // Small settle delay for dynamic content
            await Task.Delay(1000);

            return await _sharedPage.ContentAsync();
        }

        // Put this helper in the same class (or share a common helper if you already have one)
        private static string StripLeadingThe(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s ?? string.Empty;
            s = s.Trim();
            return s.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? s.Substring(4) : s;
        }

        public async Task<List<ListingData>> FetchAllPagesAsync(string itemName)
        {
            List<ListingData> allListings = new List<ListingData>();

            int maxPages = itemName == "Mann Co. Supply Crate Key" ? 1 : 2;

            for (int page = 1; page <= maxPages; page++)
            {
                string content = await FetchClassifiedsPageAsync(itemName, page);
                Console.WriteLine($"Page {page} scraped for '{itemName}' | Length: {content.Length}");

                List<ListingData> pageListings = await ExtractListingsFromPageAsync(_sharedPage);
                allListings.AddRange(pageListings);

                Console.WriteLine($" Listings added: {pageListings.Count} | 🔍 Skipped: {pageListings.Count(l => l.Price == null)}");
                await Task.Delay(1000);
            }

            if (allListings.Count == 0)
            {
                Console.WriteLine($" No listings found for {itemName}. Skipping price calculation.");
                return allListings;
            }

            Console.WriteLine($"Listings collected for '{itemName}': {allListings.Count}");

            return allListings;
        }

        private async Task<List<ListingData>> ExtractListingsFromPageAsync(IPage page)
        {
            List<ListingData> results = new List<ListingData>();

            await page.WaitForSelectorAsync("[data-listing_price]", new PageWaitForSelectorOptions
            {
                Timeout = 15000
            });

            IReadOnlyList<IElementHandle> listings = await page.QuerySelectorAllAsync("[data-listing_price]");
            Console.WriteLine($"Listings Found: {listings.Count}");
            int skipped = 0;

            foreach (IElementHandle listing in listings)
            {
                string? price = await listing.GetAttributeAsync("data-listing_price");
                string? intent = await listing.GetAttributeAsync("data-listing_intent");

                if (string.IsNullOrEmpty(price) || string.IsNullOrEmpty(intent))
                {
                    skipped++;
                    continue;
                }

                bool isBuy = intent.ToLower() == "buy";

                ListingData listingData = new ListingData
                {
                    Price = price,
                    IsBuyOrder = isBuy
                };

                results.Add(listingData);

                Console.WriteLine($"{(isBuy ? "BUY" : "SELL")} | {price} ref");
            }

            Console.WriteLine($"Listings added: {results.Count} | ❌ Skipped: {skipped}");
            return results;

        }




    }
}
