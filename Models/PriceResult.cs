namespace ArkRoxBot.Models
{

    // Class made for holding the final filtered prices
    public class PriceResult
    {
        public string Name { get; set; } = "";
        public decimal MostCommonBuyPrice { get; set; }
        public decimal MostCommonSellPrice { get; set; }
    }
}
