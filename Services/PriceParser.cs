using ArkRoxBot.Interfaces;
using ArkRoxBot.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ArkRoxBot.Services
{
    public class PriceParser : IPriceParser
    {
        private readonly IKeyPriceTracker _keyPriceTracker;

        public PriceParser(IKeyPriceTracker keyPriceTracker)
        {
            _keyPriceTracker = keyPriceTracker;
        }

        public bool TryParseToRefined(string input, out decimal totalRefined)
        {
            totalRefined = 0;

            if (string.IsNullOrWhiteSpace(input))
                return false;

            input = input.ToLowerInvariant().Trim();

            try
            {
                decimal keyPrice = _keyPriceTracker.GetCurrentSellPrice();

                // Examples:
                // "1 key, 15 ref"
                // "65.33 ref"
                // "2 keys"
                string[] parts = input.Split(',');

                foreach (string part in parts)
                {
                    string trimmed = part.Trim();

                    if (trimmed.EndsWith("ref"))
                    {
                        string refStr = trimmed.Replace("ref", "").Trim();
                        if (decimal.TryParse(refStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal refVal))
                        {
                            totalRefined += refVal;
                        }
                    }
                    else if (trimmed.EndsWith("key") || trimmed.EndsWith("keys"))
                    {
                        string keyStr = trimmed.Replace("keys", "").Replace("key", "").Trim();
                        if (decimal.TryParse(keyStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal keyCount))
                        {
                            totalRefined += keyCount * keyPrice;
                        }
                    }
                }

                return totalRefined > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
