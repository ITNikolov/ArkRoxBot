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
        private readonly PriceStore _priceStore;

        public BotService(PlaywrightScraper scraper, PriceCalculator calculator, IKeyPriceTracker keyTracker, PriceStore priceStore)
        {
            _scraper = scraper;
            _calculator = calculator;
            _keyTracker = keyTracker;
            _priceStore = priceStore;
        }

        public async Task RunAsync()
        {
            await _scraper.InitAsync();

            // Only this one time!
            string keyName = "Mann Co. Supply Crate Key";
            var keyListings = await _scraper.FetchAllPagesAsync(keyName);
            if (keyListings.Count == 0)
            {
                Console.WriteLine("⛔ No listings found for key.");
                return;
            }

            // Team Captain logic
            string hatName = "Team Captain";
            var hatListings = await _scraper.FetchAllPagesAsync(hatName);
            if (hatListings.Count > 0)
            {
                var hatResult = _calculator.Calculate(hatName, hatListings);
                _priceStore.SetPrice(hatName, hatResult);

                Console.WriteLine($"✅ Item Price Calculated → {hatName}: Buy = {hatResult.MostCommonBuyPrice} | Sell = {hatResult.MostCommonSellPrice}");
            }
        }


    }
}
