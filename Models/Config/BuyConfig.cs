﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ArkRoxBot.Models.Config
{
    public class BuyConfig
    {
        public string Name { get; set; } = "";
        public int MaxQuantity { get; set; }
        public decimal BuyPrice { get; set; }
    }
}
