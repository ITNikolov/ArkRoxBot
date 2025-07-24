using ArkRoxBot.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ArkRoxBot.Interfaces
{
    public interface IKeyPriceTracker
    {
        decimal MostCommonBuyPrice { get; set; }
        decimal MostCommonSellPrice { get; set; }
        DateTime LastUpdated { get; set; }

        PriceResult? LatestKeyPrice { get; set; }

        void UpdatePrices(PriceResult result);
        decimal GetCurrentSellPrice();
        decimal GetCurrentBuyPrice();
    }
}
