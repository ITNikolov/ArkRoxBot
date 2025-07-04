using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ArkRoxBot.Models
{
    internal class ItemConfig
    {

        public string? Name { get; set; }
        public int MaxQuantity { get; set; }
        public decimal MinSellPrice { get; set; }
        public decimal MinBuyPrice { get; set; }

    }
}
