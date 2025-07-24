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

        public async Task UpdateKeyPriceAsync()
        {
            await FetchAllPagesAsync("Mann Co. Supply Crate Key");
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

            PriceResult result = _priceCalculator.Calculate(itemName, allListings);

            if (itemName == "Mann Co. Supply Crate Key")
            {
                _keyPriceTracker.MostCommonBuyPrice = result.MostCommonBuyPrice;
                _keyPriceTracker.MostCommonSellPrice = result.MostCommonSellPrice;
                _keyPriceTracker.LastUpdated = DateTime.Now;

                Console.WriteLine($"Key Price Updated → Buy: {result.MostCommonBuyPrice} | Sell: {result.MostCommonSellPrice}");
            }
            else
            {
                Console.WriteLine($"Item Price Calculated → {itemName}: Buy = {result.MostCommonBuyPrice} | Sell = {result.MostCommonSellPrice}");
            }

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

            Console.WriteLine($" Listings added: {results.Count} | ❌ Skipped: {skipped}");
            return results;

        }




    }
}
