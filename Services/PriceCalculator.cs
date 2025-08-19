using ArkRoxBot.Helpers;
using ArkRoxBot.Interfaces;
using ArkRoxBot.Models;

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
            List<decimal> buyPrices = new();
            List<decimal> sellPrices = new();

            foreach (var listing in listings)
            {
                if (!_priceParser.TryParseToRefined(listing.Price, out decimal refinedPrice))
                {
                    Console.WriteLine($"❌ Could not parse listing price: '{listing.Price}'");
                    continue;
                }

                if (listing.IsBuyOrder)
                    buyPrices.Add(refinedPrice);
                else
                    sellPrices.Add(refinedPrice);
            }

            Console.WriteLine($"✅ Parsed BUY: {buyPrices.Count} | SELL: {sellPrices.Count}");

            foreach (var price in buyPrices)
                Console.WriteLine($"  🟢 BUY Collected: {price} ref");

            foreach (var price in sellPrices)
                Console.WriteLine($"  🔴 SELL Collected: {price} ref");

            decimal buy = CalculateSmartPrice(buyPrices, "BUY");
            decimal sell = CalculateSmartPrice(sellPrices, "SELL");

            Console.WriteLine($"🎯 Final BUY Price: {buy}");
            Console.WriteLine($"🎯 Final SELL Price: {sell}");

            return new PriceResult
            {
                Name = itemName,
                MostCommonBuyPrice = buy,
                MostCommonSellPrice = sell
            };
        }

        private decimal CalculateSmartPrice(List<decimal> prices, string label)
        {
            if (prices.Count < 5)
            {
                Console.WriteLine($"⚠️ Not enough valid {label} listings.");
                return 0;
            }

            // 1. Trim top/bottom 10%
            var sorted = prices.OrderBy(p => p).ToList();
            int trimCount = (int)(sorted.Count * 0.10m);
            var trimmed = sorted.Skip(trimCount).Take(sorted.Count - 2 * trimCount).ToList();

            if (trimmed.Count < 3)
            {
                Console.WriteLine($"⚠️ Too few {label} listings after trimming.");
                return 0;
            }

            // 2. Count frequency with bias
            Dictionary<decimal, int> frequency = new();
            foreach (var price in trimmed)
            {
                int weight = 1;

                if (label == "BUY")
                {
                    weight += (int)Math.Round(price); // bias toward higher price
                }
                else
                {
                    weight += (int)Math.Round(100 - price); // bias toward lower price
                }

                if (!frequency.ContainsKey(price))
                    frequency[price] = weight;
                else
                    frequency[price] += weight;
            }

            // 3. Pick most frequent with tie-breaker:
            // BUY → favor higher price, SELL → favor lower price
            decimal bestPrice = frequency
                .OrderByDescending(kv => kv.Value)
                .ThenByDescending(kv => label == "BUY" ? kv.Key : -kv.Key)
                .First().Key;

            return bestPrice;
        }
    }
}
