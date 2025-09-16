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
                decimal refinedPrice;
                if (!_priceParser.TryParseToRefined(listing.Price, out refinedPrice))
                {
                    Console.WriteLine("Could not parse listing price: '" + listing.Price + "'");
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

            Console.WriteLine("Parsed → Buy: " + buyPrices.Count.ToString() + ", Sell: " + sellPrices.Count.ToString());

            // 1) Determine SELL first (unchanged logic inside GetSmartPrice)
            decimal sellPrice = GetSmartPrice(sellPrices, "SELL");

            // 2) Determine BUY only from listings BELOW the chosen SELL
            decimal buyPrice;
            if (sellPrice <= 0m)
            {
                // No reliable SELL → skip BUY to avoid overpaying
                Console.WriteLine("SELL unavailable → disabling BUY for safety.");
                buyPrice = 0m;
            }
            else
            {
                // BUY window is capped at SELL inside GetSmartPrice
                buyPrice = GetSmartPrice(buyPrices, "BUY", sellPrice);
            }

            return new PriceResult
            {
                Name = itemName,
                MostCommonBuyPrice = buyPrice,
                MostCommonSellPrice = sellPrice
            };
        }


        private decimal GetSmartPrice(List<decimal> prices, string label, decimal? sellAnchor = null)
        {
            if (prices.Count < 5)
            {
                Console.WriteLine("Not enough valid " + label + " listings. Skipping.");
                return 0;
            }

            List<decimal> sorted = prices.OrderBy(p => p).ToList();
            List<decimal> trimmed;

            if (label == "BUY")
            {
                int trim = (int)(sorted.Count * 0.10m);
                trimmed = sorted.Skip(trim).Take(sorted.Count - 2 * trim).ToList();

                if (trimmed.Count < 3)
                {
                    Console.WriteLine("Too few " + label + " listings after trimming. Skipping.");
                    return 0;
                }
            }
            else
            {
                trimmed = sorted;
            }

            // median (keep your original style)
            decimal median = trimmed[trimmed.Count / 2];
            Console.WriteLine(label + " median (after trim): " + median.ToString("0.00") + " ref");

            // ---- Window selection (unchanged), with BUY capped under SELL anchor ----
            decimal[] windows = (label == "BUY")
                ? new decimal[] { 0.15m, 0.20m }
                : new decimal[] { 0.15m };

            List<decimal> pocket = new List<decimal>();
            bool pocketOk = false;

            if (label == "BUY" && sellAnchor.HasValue && sellAnchor.Value > 0m)
            {
                Console.WriteLine("BUY anchor: cap max price at SELL = " + sellAnchor.Value.ToString("0.00") + " ref");
            }

            foreach (decimal w in windows)
            {
                decimal min = median * (1m - w);
                decimal max = median * (1m + w);

                // NEW: if BUY is anchored, do not consider prices above SELL
                if (label == "BUY" && sellAnchor.HasValue && sellAnchor.Value > 0m && max > sellAnchor.Value)
                {
                    max = sellAnchor.Value;
                }

                pocket = trimmed.Where(p => p >= min && p <= max).ToList();

                if (pocket.Count >= 3)
                {
                    if (label == "BUY")
                        Console.WriteLine("BUY: using [" + min.ToString("0.00") + ", " + max.ToString("0.00") + "] with " + pocket.Count + " inliers.");
                    pocketOk = true;
                    break;
                }
                else
                {
                    if (label == "BUY")
                        Console.WriteLine("BUY: window ±" + (w * 100m).ToString("0") + "% → " + pocket.Count + " inliers (need 3).");
                }
            }

            if (!pocketOk)
            {
                Console.WriteLine("Too few " + label + " listings inside allowed window. Skipping.");
                return 0;
            }

            // ---- frequency with bias (unchanged) ----
            Dictionary<decimal, int> frequency = new Dictionary<decimal, int>();
            foreach (decimal price in pocket)
            {
                if (!frequency.ContainsKey(price))
                    frequency[price] = 0;
                frequency[price]++;
            }

            foreach (decimal price in pocket)
            {
                bool favorHighBuy = label == "BUY" && price > median;
                bool favorLowSell = label == "SELL" && price < median;

                if (favorHighBuy || favorLowSell)
                    frequency[price]++;
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
            Console.WriteLine("Final " + label + " Price: " + bestPrice + " ref");
            return bestPrice;
        }

    }
}
