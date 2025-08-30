using ArkRoxBot.CommandSystem;
using ArkRoxBot.Interfaces;
using ArkRoxBot.Models.Config;
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
        private readonly CommandService _commandService;
        private readonly ItemConfigLoader _configLoader;
        private readonly BackpackListingService _listingService;



        public BotService(
    PlaywrightScraper scraper,
    PriceCalculator calculator,
    IKeyPriceTracker keyTracker,
    PriceStore priceStore,
    CommandService commandService,
    ItemConfigLoader configLoader,
    BackpackListingService listingService)
        {
            _scraper = scraper;
            _calculator = calculator;
            _keyTracker = keyTracker;
            _priceStore = priceStore;
            _commandService = commandService;
            _configLoader = configLoader;
            _listingService = listingService;

        }

        public async Task RunAsync()
        {
            await _scraper.InitAsync();

            string keyName = "Mann Co. Supply Crate Key";
            var keyListings = await _scraper.FetchAllPagesAsync(keyName);
            if (keyListings.Count == 0)
            {
                Console.WriteLine("No listings found for key.");
                return;
            }

            ConfigRoot config = _configLoader.LoadItems();
            var itemNames = config.BuyConfig.Select(b => b.Name)
                .Concat(config.SellConfig.Select(s => s.Name))
                .Distinct();

            foreach (string itemName in itemNames)
            {
                var listings = await _scraper.FetchAllPagesAsync(itemName);
                if (listings.Count == 0)
                {
                    Console.WriteLine($"No listings found for {itemName}. Skipping.");
                    continue;
                }

                var result = _calculator.Calculate(itemName, listings);
                _priceStore.SetPrice(itemName, result);

                Console.WriteLine($"Item Price Calculated → {itemName}: Buy = {result.MostCommonBuyPrice} | Sell = {result.MostCommonSellPrice}");
            }

            await _listingService.RefreshListingsAsync(config, _priceStore);
        }



    }
}
