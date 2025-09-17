using System;
using System.Collections.Generic;
using System.Globalization;
using ArkRoxBot.Models;

namespace ArkRoxBot.Services
{
    public enum OfferDecision
    {
        Accept = 0,
        Decline = 1
    }

    public sealed class OfferEvaluationResult
    {
        public OfferDecision Decision { get; set; }
        public string Reason { get; set; } = string.Empty;
        public decimal ReceiveRef { get; set; }
        public decimal GiveRef { get; set; }
        public decimal ProfitRef { get; set; }
    }

    public sealed class OfferEvaluator
    {
        private readonly PriceStore _priceStore;
        private readonly decimal _profitBufferRef;

        private const string KeyName = "Mann Co. Supply Crate Key";
        private const string RefinedName = "Refined Metal";
        private const string ReclaimedName = "Reclaimed Metal";
        private const string ScrapName = "Scrap Metal";

        private const decimal RefinedValue = 1.00m;
        private const decimal ReclaimedValue = 0.33m;
        private const decimal ScrapValue = 0.11m;

        public OfferEvaluator(PriceStore priceStore, decimal profitBufferRef = 0.11m)
        {
            _priceStore = priceStore;
            _profitBufferRef = profitBufferRef;
        }

        public OfferEvaluationResult Evaluate(OfferSummary summary)
        {
            decimal receive = 0m;
            decimal give = 0m;

            List<string> reasons = new List<string>();

            // Value what we RECEIVE at our SELL prices (or pure)
            foreach (KeyValuePair<string, int> kv in summary.ItemsToReceiveByName)
            {
                string name = kv.Key;
                int count = kv.Value;

                decimal perUnit;
                bool ok = TryGetSellValueRef(name, out perUnit);
                if (!ok)
                {
                    reasons.Add("unknown/unsellable receive: " + name);
                    continue;
                }
                receive += perUnit * count;
            }

            // Value what we GIVE at our BUY prices (or pure)
            foreach (KeyValuePair<string, int> kv in summary.ItemsToGiveByName)
            {
                string name = kv.Key;
                int count = kv.Value;

                decimal perUnit;
                bool ok = TryGetBuyCostRef(name, out perUnit);
                if (!ok)
                {
                    reasons.Add("unknown/unpriced cost: " + name);
                    continue;
                }
                give += perUnit * count;
            }

            decimal profit = receive - give;

            OfferEvaluationResult result = new OfferEvaluationResult
            {
                ReceiveRef = receive,
                GiveRef = give,
                ProfitRef = profit
            };

            bool hasUnknown = reasons.Count > 0;

            if (hasUnknown)
            {
                result.Decision = OfferDecision.Decline;
                result.Reason = string.Join("; ", reasons);
                return result;
            }

            if (profit >= _profitBufferRef)
            {
                result.Decision = OfferDecision.Accept;
                result.Reason = "profit " + profit.ToString("0.00", CultureInfo.InvariantCulture)
                              + " ≥ buffer " + _profitBufferRef.ToString("0.00", CultureInfo.InvariantCulture) + " ref";
            }
            else
            {
                result.Decision = OfferDecision.Decline;
                result.Reason = "profit " + profit.ToString("0.00", CultureInfo.InvariantCulture)
                              + " < buffer " + _profitBufferRef.ToString("0.00", CultureInfo.InvariantCulture) + " ref";
            }

            return result;
        }

        private bool TryGetSellValueRef(string name, out decimal perUnit)
        {
            // Pure at face value
            if (string.Equals(name, RefinedName, StringComparison.OrdinalIgnoreCase)) { perUnit = RefinedValue; return true; }
            if (string.Equals(name, ReclaimedName, StringComparison.OrdinalIgnoreCase)) { perUnit = ReclaimedValue; return true; }
            if (string.Equals(name, ScrapName, StringComparison.OrdinalIgnoreCase)) { perUnit = ScrapValue; return true; }

            // Keys via SELL price
            if (string.Equals(name, KeyName, StringComparison.OrdinalIgnoreCase))
            {
                PriceResult priced;
                if (_priceStore.TryGetPrice(KeyName, out priced) && priced.MostCommonSellPrice > 0m)
                {
                    perUnit = priced.MostCommonSellPrice;
                    return true;
                }
                perUnit = 0m;
                return false;
            }

            // Normal items via SELL price
            PriceResult item;
            if (_priceStore.TryGetPrice(name, out item) && item.MostCommonSellPrice > 0m)
            {
                perUnit = item.MostCommonSellPrice;
                return true;
            }

            perUnit = 0m;
            return false;
        }

        private bool TryGetBuyCostRef(string name, out decimal perUnit)
        {
            // Pure at face value
            if (string.Equals(name, RefinedName, StringComparison.OrdinalIgnoreCase)) { perUnit = RefinedValue; return true; }
            if (string.Equals(name, ReclaimedName, StringComparison.OrdinalIgnoreCase)) { perUnit = ReclaimedValue; return true; }
            if (string.Equals(name, ScrapName, StringComparison.OrdinalIgnoreCase)) { perUnit = ScrapValue; return true; }

            // Keys via BUY price
            if (string.Equals(name, KeyName, StringComparison.OrdinalIgnoreCase))
            {
                PriceResult priced;
                if (_priceStore.TryGetPrice(KeyName, out priced) && priced.MostCommonBuyPrice > 0m)
                {
                    perUnit = priced.MostCommonBuyPrice;
                    return true;
                }
                perUnit = 0m;
                return false;
            }

            // Normal items via BUY price
            PriceResult item;
            if (_priceStore.TryGetPrice(name, out item) && item.MostCommonBuyPrice > 0m)
            {
                perUnit = item.MostCommonBuyPrice;
                return true;
            }

            perUnit = 0m;
            return false;
        }
    }
}
