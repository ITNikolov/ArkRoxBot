using System;
using System.Collections.Generic;
using ArkRoxBot.Models;
using ArkRoxBot.Services;

namespace ArkRoxBot.Services
{
    public sealed class CommandService
    {
        private readonly PriceStore _priceStore;

        public CommandService(PriceStore priceStore)
        {
            _priceStore = priceStore;
        }

        public string HandleCommand(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return string.Empty;

            string text = message.Trim();

            if (text.Equals("!help", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("!how2trade", StringComparison.OrdinalIgnoreCase))
            {
                return "Available Commands: !price <item>, !buy <item>, !sell <item>, !owner, !help, !status";
            }

            if (text.StartsWith("!price ", StringComparison.OrdinalIgnoreCase))
                return HandlePrice(text.Substring(7).Trim());

            if (text.StartsWith("!buy ", StringComparison.OrdinalIgnoreCase))
                return HandleUserBuysFromUs(text.Substring(5).Trim());   // uses SELL

            if (text.StartsWith("!sell ", StringComparison.OrdinalIgnoreCase))
                return HandleUserSellsToUs(text.Substring(6).Trim());     // uses BUY

            if (text.Equals("!owner", StringComparison.OrdinalIgnoreCase))
                return "Owner: https://steamcommunity.com/id/yourprofile/"; // adjust

            if (text.Equals("!status", StringComparison.OrdinalIgnoreCase))
                return "Online and pricing. Type !help for commands.";

            return string.Empty;
        }

        private string HandlePrice(string item)
        {
            PriceResult? p;
            if (!TryGetPriceInsensitive(item, out p) || p == null)
                return "I don’t have a price for '" + item + "' right now.";

            string buy = p.MostCommonBuyPrice > 0 ? p.MostCommonBuyPrice.ToString("0.00") + " ref" : "—";
            string sell = p.MostCommonSellPrice > 0 ? p.MostCommonSellPrice.ToString("0.00") + " ref" : "—";
            return item + " — BUY: " + buy + " | SELL: " + sell;
        }

        // User says "!buy <item>" → user wants to buy from us → show SELL price.
        private string HandleUserBuysFromUs(string item)
        {
            PriceResult? p;
            if (!TryGetPriceInsensitive(item, out p) || p == null || p.MostCommonSellPrice <= 0)
                return "I’m not selling '" + item + "' right now.";

            string amt = p.MostCommonSellPrice.ToString("0.00");
            return "To buy '" + item + "', I sell it for " + amt + " ref. "
                 + "Send a trade offer and I’ll handle it soon.";
            // next step: auto-create the offer here via TradeService
        }

        // User says "!sell <item>" → user wants to sell to us → show BUY price.
        private string HandleUserSellsToUs(string item)
        {
            PriceResult? p;
            if (!TryGetPriceInsensitive(item, out p) || p == null || p.MostCommonBuyPrice <= 0)
                return "I’m not buying '" + item + "' right now.";

            string amt = p.MostCommonBuyPrice.ToString("0.00");
            return "To sell '" + item + "', I pay " + amt + " ref if the item matches. "
                 + "Send a trade offer and I’ll handle it soon.";
            // next step: auto-accept based on TradeService checks
        }

        private bool TryGetPriceInsensitive(string name, out PriceResult? result)
        {
            IReadOnlyDictionary<string, PriceResult> all = _priceStore.GetAllPrices();
            foreach (KeyValuePair<string, PriceResult> kv in all)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    result = kv.Value;
                    return true;
                }
            }
            result = null;
            return false;
        }
    }
}
