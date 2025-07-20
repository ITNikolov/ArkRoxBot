using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ArkRoxBot.Models
{
    public class KeyPriceTracker
    {
        public decimal MostCommonBuyPrice { get; set; }
        public decimal MostCommonSellPrice { get; set; }

        public DateTime LastUpdated { get; set; }
    }

}
