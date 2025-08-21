using ArkRoxBot.Interfaces;
using System;

namespace ArkRoxBot.Models
{
    public class KeyPriceTracker : IKeyPriceTracker
    {
        public decimal MostCommonBuyPrice { get; set; }
        public decimal MostCommonSellPrice { get; set; }
        public DateTime LastUpdated { get; set; }
        public PriceResult? LatestKeyPrice { get; set; }


        public void UpdatePrices(PriceResult result)
        {
            MostCommonBuyPrice = result.MostCommonBuyPrice;
            MostCommonSellPrice = result.MostCommonSellPrice;
            LastUpdated = DateTime.Now;

            Console.WriteLine($"Key Price Updated → Buy = {MostCommonBuyPrice} | Sell = {MostCommonSellPrice} (as of {LastUpdated})");
        }


        public decimal GetCurrentSellPrice()
        {
            return MostCommonSellPrice;
        }

        public decimal GetCurrentBuyPrice()
        {
            return MostCommonBuyPrice;
        }
    }
}
