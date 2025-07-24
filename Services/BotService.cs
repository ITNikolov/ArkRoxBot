using ArkRoxBot.Interfaces;
using System;
using System.Threading.Tasks;

namespace ArkRoxBot.Services
{
    public class BotService
    {
        private readonly PlaywrightScraper _scraper;
        private readonly PriceCalculator _calculator;
        private readonly IKeyPriceTracker _keyTracker;

        public BotService(PlaywrightScraper scraper, PriceCalculator calculator, IKeyPriceTracker keyTracker)
        {
            _scraper = scraper;
            _calculator = calculator;
            _keyTracker = keyTracker;
        }

        public async Task RunAsync()
        {
            await _scraper.InitAsync();

            string keyName = "Mann Co. Supply Crate Key";
            var listings = await _scraper.FetchAllPagesAsync(keyName);

            if (listings.Count == 0)
            {
                Console.WriteLine("⚠️ No listings found for key.");
                return;
            }

            var result = _calculator.Calculate(keyName, listings);
            _keyTracker.UpdatePrices(result); // <-- NEW

            // Test scrape for another item
            await _scraper.FetchAllPagesAsync("Team Captain");
        }
    }
}
