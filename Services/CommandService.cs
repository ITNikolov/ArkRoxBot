using ArkRoxBot.Models;
using ArkRoxBot.Services;

namespace ArkRoxBot.CommandSystem
{
    public class CommandService
    {
        private readonly PriceStore _priceStore;

        public CommandService(PriceStore priceStore)
        {
            _priceStore = priceStore;
        }

        public string HandleCommand(string input)
        {
            input = input.Trim();

            if (input.StartsWith("!price", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    return "Please provide an item name. Example: !price Team Captain";

                string itemName = parts[1];
                if (_priceStore.GetPrice(itemName) is PriceResult price)
                {
                    return $"{itemName} → Buy Price: {price.MostCommonBuyPrice} ref | Sell Price: {price.MostCommonSellPrice} ref";
                }
                else
                {
                    return $"No price found for '{itemName}'. Maybe it's not scraped yet?";
                }
            }

            if (input.StartsWith("!buy", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    return "Please provide the item name. Example: !buy Team Captain";

                string itemName = parts[1];
                if (_priceStore.GetPrice(itemName) is PriceResult price)
                {
                    return $"To buy '{itemName}', please list your item for {price.MostCommonBuyPrice} ref and I will buy it soon.";
                }
                else
                {
                    return $"No price found for '{itemName}'. Can't process buy request.";
                }
            }

            if (input.StartsWith("!sell", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    return "Please provide the item name. Example: !sell Team Captain";

                string itemName = parts[1];
                if (_priceStore.GetPrice(itemName) is PriceResult price)
                {
                    return $"To sell '{itemName}', please list it for {price.MostCommonSellPrice} ref and I will buy it soon.";
                }
                else
                {
                    return $"No price found for '{itemName}'. Can't process sell request.";
                }
            }

            if (input.Equals("!owner", StringComparison.OrdinalIgnoreCase))
            {
                return "Bot Owner Profile: https://steamcommunity.com/profiles/76561198085806375/";
            }

            if (input.Equals("!help", StringComparison.OrdinalIgnoreCase))
            {
                return "Available Commands: !price <item>, !buy <item>, !sell <item>, !owner, !help, !status";
            }

            if (input.Equals("!status", StringComparison.OrdinalIgnoreCase))
            {
                return "Bot is online and running.";
            }

            return "Unknown command. Type !help for more info.";
        }

        public string GetWelcomeMessage()
        {
            return "Hello! This is an automated trading bot. Type !help to see available commands. Please note: All trades are final. No refunds.";
        }
    }
}
