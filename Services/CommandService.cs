using System.Globalization;
using ArkRoxBot.Interfaces;
using ArkRoxBot.Models;
using Microsoft.Extensions.Configuration;

namespace ArkRoxBot.Services
{
    public sealed class CommandService
    {
        private readonly PriceStore _priceStore;
        private readonly InventoryService _inventory;
        private readonly ITradeService _trade;
        private readonly string _manualTradeUrl;




        public CommandService(PriceStore priceStore, InventoryService inventory, ITradeService trade,IConfiguration cfg)
        {
            _priceStore = priceStore;
            _inventory = inventory;
            _trade = trade;

            string tradeUrl = cfg["Steam:TradeUrl"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(tradeUrl))
            {
                string env = Environment.GetEnvironmentVariable("BOT_TRADE_URL") ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(env)) tradeUrl = env;
            }
            if (string.IsNullOrWhiteSpace(tradeUrl))
            {
                string botId = cfg["Steam:BotId"] ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(botId))
                    tradeUrl = "https://steamcommunity.com/profiles/" + botId + "/tradeoffers/";
            }

            _manualTradeUrl = tradeUrl;
        }

        public async Task<string> HandleCommandAsync(string message, string partnerSteamId64)
        {
            if (string.IsNullOrWhiteSpace(message)) return string.Empty;
            string text = message.Trim();

            if (text.Equals("!help", StringComparison.OrdinalIgnoreCase))
                return "Commands: !price <item>, !buy <item>, !sell <item>, !status";

            if (text.Equals("!status", StringComparison.OrdinalIgnoreCase))
                return HandleStatus();

            if (text.StartsWith("!price ", StringComparison.OrdinalIgnoreCase))
            {
                string item = text.Substring(7).Trim();
                PriceResult price;
                if (!_priceStore.TryGetPrice(item, out price))
                    return "No price for '" + item + "' yet.";

                string buy = price.MostCommonBuyPrice > 0m ? price.MostCommonBuyPrice.ToString("0.00") + " ref" : "—";
                string sell = price.MostCommonSellPrice > 0m ? price.MostCommonSellPrice.ToString("0.00") + " ref" : "—";
                return item + " — BUY: " + buy + " | SELL: " + sell;
            }

            // User wants to BUY from us → use SELL price
            if (text.StartsWith("!buy ", StringComparison.OrdinalIgnoreCase))
            {
                string item = text.Substring(5).Trim();
                PriceResult price;
                if (!_priceStore.TryGetPrice(item, out price) || price.MostCommonSellPrice <= 0m)
                    return "I’m not selling '" + item + "' right now. " + ManualTradeHint();

                (bool Ok, string? OfferId, string Reason) result =
                    await _trade.CreateSellOfferAsync(partnerSteamId64, item, price.MostCommonSellPrice);

                if (result.Ok)
                    return "Sent you an offer to SELL '" + item + "' for " +
                           price.MostCommonSellPrice.ToString("0.00") + " ref. (id: " +
                           (result.OfferId ?? "unknown") + ")";

                return "I couldn’t create the offer automatically (" + result.Reason + "). " + ManualTradeHint();
            }

            // User wants to SELL to us → use BUY price
            if (text.StartsWith("!sell ", StringComparison.OrdinalIgnoreCase))
            {
                string item = text.Substring(6).Trim();
                PriceResult price;
                if (!_priceStore.TryGetPrice(item, out price) || price.MostCommonBuyPrice <= 0m)
                    return "I’m not buying '" + item + "' right now. " + ManualTradeHint();

                (bool Ok, string? OfferId, string Reason) result =
                    await _trade.CreateBuyOfferAsync(partnerSteamId64, item, price.MostCommonBuyPrice);

                if (result.Ok)
                    return "Sent you an offer to BUY '" + item + "' for " +
                           price.MostCommonBuyPrice.ToString("0.00") + " ref. (id: " +
                           (result.OfferId ?? "unknown") + ")";

                return "I couldn’t create the offer automatically (" + result.Reason + "). " + ManualTradeHint();
            }

            return string.Empty;
        }


        private string HandleStatus()
        {
            try
            {
                var snap = _inventory.GetSnapshotAsync().GetAwaiter().GetResult();
                var all = _priceStore.GetAllPrices();
                var last = _priceStore.LastUpdatedUtc == DateTime.MinValue
                    ? "n/a"
                    : _priceStore.LastUpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

                return $"Stock — Keys: {snap.Keys} | Refined: {snap.Refined} (Recl: {snap.Reclaimed}, Scrap: {snap.Scrap})"
                     + $" | Priced items: {all.Count} | Last price refresh: {last}";
            }
            catch (Exception ex)
            {
                Console.WriteLine("[CommandService:!status] " + ex.Message);
                return "Status unavailable right now.";
            }
        }

        private string ManualTradeHint()
        {
            return string.IsNullOrEmpty(_manualTradeUrl)
                ? "You can still send me a trade offer manually from my profile."
                : $"You can still send me a trade offer here: {_manualTradeUrl}";
        }
    }
}
