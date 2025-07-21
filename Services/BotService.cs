using ArkRoxBot.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
            await _keyTracker.UpdateKeyPriceAsync();
            await _scraper.FetchAllPagesAsync("Team Captain");

            // logic to scrape item prices or test
        }
    }

}
