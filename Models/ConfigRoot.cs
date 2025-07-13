using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ArkRoxBot.Models
{
    public class ConfigRoot
    {
            public List<BuyConfig> BuyConfig { get; set; } = new();
            public List<SellConfig> SellConfig { get; set; } = new();
    }
}
