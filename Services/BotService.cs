using ArkRoxBot.Interfaces;
using ArkRoxBot.Models;
using ArkRoxBot.Models.Config;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace ArkRoxBot.Services
{
    public class BotService
    {
        private readonly ISteamClientService _steam;
        private readonly PlaywrightScraper _scraper;
        private readonly PriceCalculator _calculator;
        private readonly IKeyPriceTracker _keyTracker;
        private readonly PriceStore _priceStore;
        private readonly CommandService _commandService;
        private readonly ItemConfigLoader _configLoader;
        private readonly BackpackListingService _listingService;
        private readonly TradeService _trade;
        private readonly string _ownerSteamId64;


        public BotService(
            ISteamClientService steam,
            PlaywrightScraper scraper,
            PriceCalculator calculator,
            IKeyPriceTracker keyTracker,
            PriceStore priceStore,
            CommandService commandService,
            ItemConfigLoader configLoader,
            BackpackListingService listingService,
            TradeService trade)
        {
            _steam = steam;
            _scraper = scraper;
            _calculator = calculator;
            _keyTracker = keyTracker;
            _priceStore = priceStore;
            _commandService = commandService;
            _configLoader = configLoader;
            _listingService = listingService;
            _trade = trade;

            _ownerSteamId64 = (Environment.GetEnvironmentVariable("OWNER_STEAMID") ?? "765611988085806375").Trim();


        }

        private bool _chatWired = false;

        private void EnsureChatWired()
        {
            if (_chatWired) return;

            _steam.OnFriendMessage += async (string steamId, string text) =>
            {
                try
                {
                    string msg = (text ?? string.Empty).Trim();

                    // --- Owner-only toggle for trusted auto-accept ----------------------
                    // Owner SteamID can come from env var or your _ownerSteamId64 field.
                    string configuredOwner = (Environment.GetEnvironmentVariable("OWNER_STEAMID") ?? _ownerSteamId64 ?? string.Empty).Trim();

                    if (msg.StartsWith("!trust", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrEmpty(configuredOwner))
                        {
                            _steam.SendMessage(steamId, "Owner not configured. Set OWNER_STEAMID environment variable.");
                            return;
                        }

                        if (!string.Equals(steamId, configuredOwner, StringComparison.Ordinal))
                        {
                            _steam.SendMessage(steamId, "This command is owner-only.");
                            return;
                        }

                        if (msg.Equals("!trust status", StringComparison.OrdinalIgnoreCase))
                        {
                            bool on = _trade.GetTrustedAcceptEnabled();
                            _steam.SendMessage(steamId, "Trusted auto-accept is " + (on ? "ON" : "OFF") + ".");
                            return;
                        }
                        if (msg.Equals("!trust on", StringComparison.OrdinalIgnoreCase))
                        {
                            _trade.SetTrustedAcceptEnabled(true);
                            _steam.SendMessage(steamId, "Trusted auto-accept: ON");
                            return;
                        }
                        if (msg.Equals("!trust off", StringComparison.OrdinalIgnoreCase))
                        {
                            _trade.SetTrustedAcceptEnabled(false);
                            _steam.SendMessage(steamId, "Trusted auto-accept: OFF");
                            return;
                        }

                        _steam.SendMessage(steamId, "Usage: !trust on | !trust off | !trust status");
                        return;
                    }
                    // --------------------------------------------------------------------

                    // Your existing command handling
                    string reply = await _commandService.HandleCommandAsync(msg, steamId);
                    if (!string.IsNullOrWhiteSpace(reply))
                        _steam.SendMessage(steamId, reply);
                }
                catch (Exception ex)
                {
                    _steam.SendMessage(steamId, "Sorry, that command is unknown.");
                    Console.WriteLine("Command error: " + ex);
                }
            };

            _chatWired = true;
        }


        // Fallback SELL: if we couldn’t compute one this run, use previous stored SELL (if any)
        private void FillMissingSellFromPrevious(string itemName, PriceResult result)
        {
            if (result.MostCommonSellPrice > 0)
                return;

            if (_priceStore.TryGetPrice(itemName, out PriceResult previous) &&
                previous.MostCommonSellPrice > 0)
            {
                result.MostCommonSellPrice = previous.MostCommonSellPrice;
                Console.WriteLine("[FALLBACK] " + itemName + " SELL → using previous: " + previous.MostCommonSellPrice.ToString("0.00"));
            }
        }



        public async Task RunAsync()
        {
            EnsureChatWired();
            await _scraper.InitAsync();

            string keyName = "Mann Co. Supply Crate Key";
            List<ListingData> keyListings = await _scraper.FetchAllPagesAsync(keyName);
            if (keyListings.Count == 0)
            {
                Console.WriteLine("No listings found for key.");
                return;
            }

            // NEW: calculate, track, store, and log the KEY price
            PriceResult keyResult = _calculator.Calculate(keyName, keyListings);
            _keyTracker.UpdatePrices(keyResult);                   // so PriceParser can use current key sell price
            _priceStore.SetPrice(keyName, keyResult);              // so it appears in summary / !price
            string keyBuyText = keyResult.MostCommonBuyPrice > 0 ? keyResult.MostCommonBuyPrice.ToString("0.00") : "—";
            string keySellText = keyResult.MostCommonSellPrice > 0 ? keyResult.MostCommonSellPrice.ToString("0.00") : "—";
            Console.WriteLine("[FINAL] " + keyName + "  BUY: " + keyBuyText + " | SELL: " + keySellText);

            // then proceed with the rest of the items
            ConfigRoot config = _configLoader.LoadItems();
            IEnumerable<string> itemNames = config.BuyConfig.Select(b => b.Name)
                .Concat(config.SellConfig.Select(s => s.Name))
                .Distinct();

            foreach (string itemName in itemNames)
            {
                List<ListingData> listings = await _scraper.FetchAllPagesAsync(itemName);
                if (listings.Count == 0)
                {
                    Console.WriteLine("No listings found for " + itemName + ". Skipping.");
                    continue;
                }

                PriceResult result = _calculator.Calculate(itemName, listings);

                // SELL fallback for normal items
                FillMissingSellFromPrevious(itemName, result);
                _priceStore.SetPrice(itemName, result);


                string buyText = result.MostCommonBuyPrice > 0 ? result.MostCommonBuyPrice.ToString("0.00") : "—";
                string sellText = result.MostCommonSellPrice > 0 ? result.MostCommonSellPrice.ToString("0.00") : "—";
                Console.WriteLine("[FINAL] " + itemName + "  BUY: " + buyText + " | SELL: " + sellText);
            }

            // Summary after everything (now includes the key)
            PrintPriceSummary();

            await _listingService.RefreshListingsAsync(config, _priceStore);
        }


        private void PrintPriceSummary()
        {
            IReadOnlyDictionary<string, PriceResult> snapshot = _priceStore.GetAllPrices();
            IEnumerable<KeyValuePair<string, PriceResult>> ordered =
                snapshot.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase);

            Console.WriteLine();
            Console.WriteLine("========== PRICE SUMMARY (ref) ==========");
            Console.WriteLine(string.Format("{0,-40} {1,12} {2,12}  {3}", "Item", "BUY", "SELL", "Notes"));
            Console.WriteLine(new string('-', 80));

            foreach (KeyValuePair<string, PriceResult> kv in ordered)
            {
                string itemName = kv.Key;
                PriceResult result = kv.Value;

                string buy = result.MostCommonBuyPrice > 0 ? result.MostCommonBuyPrice.ToString("0.00") : "—";
                string sell = result.MostCommonSellPrice > 0 ? result.MostCommonSellPrice.ToString("0.00") : "—";

                string note = string.Empty;
                if (result.MostCommonBuyPrice > 0 && result.MostCommonSellPrice > 0 &&
                    result.MostCommonBuyPrice >= result.MostCommonSellPrice)
                {
                    note = "buy ≥ sell";
                }

                Console.WriteLine(string.Format("{0,-40} {1,12} {2,12}  {3}", itemName, buy, sell, note));
            }

            Console.WriteLine(new string('-', 80));
            Console.WriteLine("End of summary.");
        }
    }
}
