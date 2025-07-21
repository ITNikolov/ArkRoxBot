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
            List<decimal> buyPrices = new List<decimal>();
            List<decimal> sellPrices = new List<decimal>();

            foreach (ListingData listing in listings)
            {
                if (!_priceParser.TryParseToRefined(listing.Price, out decimal refinedPrice))
                {
                    Console.WriteLine($"❌ Could not parse listing price: '{listing.Price}'");
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

            decimal mostCommonBuyPrice = GetMostFrequentPrice(buyPrices);
            decimal mostCommonSellPrice = GetMostFrequentPrice(sellPrices);

            Console.WriteLine($"✅ Parsed BUY: {buyPrices.Count} | SELL: {sellPrices.Count}");

            foreach (decimal price in buyPrices)
            {
                Console.WriteLine($"  🟢 BUY Collected: {price} ref");
            }

            foreach (decimal price in sellPrices)
            {
                Console.WriteLine($"  🔴 SELL Collected: {price} ref");
            }

            return new PriceResult
            {
                Name = itemName,
                MostCommonBuyPrice = mostCommonBuyPrice,
                MostCommonSellPrice = mostCommonSellPrice
            };
        }




        private decimal GetMostFrequentPrice(List<decimal> prices)
        {
            Dictionary<decimal, int> frequencyMap = new Dictionary<decimal, int>();

            foreach (decimal price in prices)
            {
                if (!frequencyMap.ContainsKey(price))
                {
                    frequencyMap[price] = 1;
                }
                else
                {
                    frequencyMap[price]++;
                }
            }

            decimal mostFrequent = 0;
            int highestCount = 0;

            foreach (KeyValuePair<decimal, int> entry in frequencyMap)
            {
                if (entry.Value > highestCount)
                {
                    highestCount = entry.Value;
                    mostFrequent = entry.Key;
                }
            }

            return mostFrequent;


        }
    }
}
