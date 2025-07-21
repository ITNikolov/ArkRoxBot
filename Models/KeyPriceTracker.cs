using ArkRoxBot.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ArkRoxBot.Models
{
    public class KeyPriceTracker : IKeyPriceTracker
    {
        
        public decimal MostCommonBuyPrice { get; set; }
        public decimal MostCommonSellPrice { get; set; }

        public DateTime LastUpdated { get; set; }

        public PriceResult? LatestKeyPrice { get; set; }

        public Task UpdateKeyPriceAsync()
        {
            throw new NotImplementedException();
        }
        public decimal GetCurrentSellPrice()
        {
            return LatestKeyPrice?.MostCommonSellPrice ?? 0;
        }

        public decimal GetCurrentBuyPrice()
        {
            return LatestKeyPrice?.MostCommonBuyPrice ?? 0;
        }

        
    }

}
