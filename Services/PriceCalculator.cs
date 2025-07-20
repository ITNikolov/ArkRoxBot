using ArkRoxBot.Models;

namespace ArkRoxBot.Services
{
    public class PriceCalculator
    {
        public PriceResult Calculate(string itemName, List<ListingData> listings)
        {
            List<decimal> buyPrices = new List<decimal>();
            List<decimal> sellPrices = new List<decimal>();

            foreach (ListingData listing in listings)
            {
                // Skip if price is null or empty
                if (string.IsNullOrWhiteSpace(listing.Price))
                {
                    continue;
                }

                string cleanedPrice = listing.Price.Trim().ToLower();

                // Skip key-only listings (e.g., "45 keys")
                if (cleanedPrice.Contains("key") && !cleanedPrice.Contains("ref"))
                {
                    Console.WriteLine($"❌ Skipped key-only price: '{listing.Price}'");
                    continue;
                }

                // Try to extract just the "ref" portion from mixed price (e.g., "2 keys, 12 ref")
                string refPart = cleanedPrice;

                if (cleanedPrice.Contains(","))
                {
                    string[] parts = cleanedPrice.Split(',');

                    foreach (string part in parts)
                    {
                        if (part.Contains("ref"))
                        {
                            refPart = part;
                            break;
                        }
                    }
                }

                // Remove "ref" and whitespace
                refPart = refPart.Replace("ref", "").Trim();

                decimal parsedPrice;
                bool isParsed = decimal.TryParse(
                    refPart,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out parsedPrice
                );

                if (!isParsed)
                {
                    Console.WriteLine($"❌ Could not parse cleaned price: '{refPart}' (from '{listing.Price}')");
                    continue;
                }

                if (listing.IsBuyOrder)
                {
                    buyPrices.Add(parsedPrice);
                }
                else
                {
                    sellPrices.Add(parsedPrice);
                }
            }

            decimal mostCommonBuyPrice = GetMostFrequentPrice(buyPrices);
            decimal mostCommonSellPrice = GetMostFrequentPrice(sellPrices);

            Console.WriteLine($"✅ Parsed Buy Prices: {buyPrices.Count}, Sell Prices: {sellPrices.Count}");

            foreach (decimal price in buyPrices)
            {
                Console.WriteLine($"  🟢 BUY Collected: {price}");
            }

            foreach (decimal price in sellPrices)
            {
                Console.WriteLine($"  🔴 SELL Collected: {price}");
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
