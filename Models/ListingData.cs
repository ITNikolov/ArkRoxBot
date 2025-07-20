namespace ArkRoxBot.Models
{
    // Class made to hold scraped listings before filtering
    public class ListingData
    {
        public string Price { get; set; } = "";
        public bool IsBuyOrder { get; set; }
    }
}
