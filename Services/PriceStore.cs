using ArkRoxBot.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ArkRoxBot.Services
{
    public class PriceStore
    {
        private readonly ConcurrentDictionary<string, PriceResult> _prices = new();

        public void SetPrice(string itemName, PriceResult result)
        {
            _prices[itemName.ToLower()] = result;
        }

        public PriceResult? GetPrice(string itemName)
        {
            _prices.TryGetValue(itemName.ToLower(), out var result);
            return result;
        }

        public bool TryGetPrice(string itemName, out PriceResult result)
        {
            return _prices.TryGetValue(itemName.ToLower(), out result);
        }

        public IReadOnlyDictionary<string, PriceResult> GetAllPrices()
        {
            return _prices;
        }
    }
}
