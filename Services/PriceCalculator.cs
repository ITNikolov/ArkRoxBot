using ArkRoxBot.Helpers;
using ArkRoxBot.Interfaces;
using ArkRoxBot.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArkRoxBot.Services
{
    public class PriceCalculator
    {
        private readonly IPriceParser _priceParser;

        public PriceCalculator(IPriceParser priceParser)
        {
            _priceParser = priceParser;
        }

        public PriceResult Calculate(string itemName, List<ListingData> listings)
        {
            List<decimal> buyPrices = new List<decimal>();
            List<decimal> sellPrices = new List<decimal>();

            foreach (ListingData listing in listings)
            {
                if (!_priceParser.TryParseToRefined(listing.Price, out decimal refinedPrice))
                {
                    Console.WriteLine($"Could not parse listing price: '{listing.Price}'");
                    continue;
                }

                if (listing.IsBuyOrder)
                {
                    buyPrices.Add(refinedPrice);
                }
                else
                {
                    sellPrices.Add(refinedPrice);
                }
            }

            Console.WriteLine($"Parsed → Buy: {buyPrices.Count}, Sell: {sellPrices.Count}");

            decimal buyPrice = GetSmartPrice(buyPrices, "BUY");
            decimal sellPrice = GetSmartPrice(sellPrices, "SELL");

            Console.WriteLine($"Final BUY Price: {buyPrice} ref");
            Console.WriteLine($"Final SELL Price: {sellPrice} ref");

            return new PriceResult
            {
                Name = itemName,
                MostCommonBuyPrice = buyPrice,
                MostCommonSellPrice = sellPrice
            };
        }

        private decimal GetSmartPrice(List<decimal> prices, string label)
        {
            if (prices.Count < 5)
            {
                Console.WriteLine($"Not enough valid {label} listings. Skipping.");
                return 0;
            }

            List<decimal> sorted = prices.OrderBy(p => p).ToList();
            int trim = (int)(sorted.Count * 0.10m);
            List<decimal> trimmed = sorted.Skip(trim).Take(sorted.Count - 2 * trim).ToList();

            if (trimmed.Count < 3)
            {
                Console.WriteLine($"Too few {label} listings after trimming. Skipping.");
                return 0;
            }

            decimal median = trimmed[trimmed.Count / 2];
            decimal windowSize = median * 0.15m;
            decimal min = median - windowSize;
            decimal max = median + windowSize;

            List<decimal> pocket = trimmed.Where(p => p >= min && p <= max).ToList();

            if (pocket.Count < 3)
            {
                Console.WriteLine($"Too few {label} listings inside ±15% range. Skipping.");
                return 0;
            }

            Dictionary<decimal, int> frequency = new Dictionary<decimal, int>();

            foreach (decimal price in pocket)
            {
                if (!frequency.ContainsKey(price))
                {
                    frequency[price] = 0;
                }

                frequency[price]++;
            }

            // Add +1 frequency bias to higher-than-median (BUY) or lower-than-median (SELL)
            foreach (decimal price in pocket)
            {
                bool favorHighBuy = label == "BUY" && price > median;
                bool favorLowSell = label == "SELL" && price < median;

                if (favorHighBuy || favorLowSell)
                {
                    frequency[price]++;
                }
            }

            IOrderedEnumerable<KeyValuePair<decimal, int>> ordered;

            if (label == "BUY")
            {
                ordered = frequency
                    .OrderByDescending(pair => pair.Value)
                    .ThenByDescending(pair => pair.Key);
            }
            else
            {
                ordered = frequency
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key);
            }

            decimal bestPrice = ordered.First().Key;

            Console.WriteLine($"Final {label} Price: {bestPrice} ref");
            return bestPrice;
        }
    }
}
