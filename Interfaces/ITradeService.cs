namespace ArkRoxBot.Interfaces
{
    public interface ITradeService
    {
        public void Start();
        public void Stop();

        public Task<bool> VerifyCommunityCookiesOnceAsync();
        public Task LogStockSnapshotOnceAsync();

        // Partner sells to us → we pay pure (already implemented)
        public Task<(bool Ok, string? OfferId, string Reason)> CreateBuyOfferAsync(
            string partnerSteamId64, string itemName, decimal payRef);

        // Partner buys from us → they pay pure (add this)
        public Task<(bool Ok, string? OfferId, string Reason)> CreateSellOfferAsync(
            string partnerSteamId64, string itemName, decimal priceRef);
    }
}
