﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ArkRoxBot.Models.Config
{
    public class SellConfig
    {
        public string Name { get; set; } = "";
        public int Quantity { get; set; }
        public decimal MinSellPrice { get; set; }
        public decimal SellPrice { get; set; }
    }
}
