using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ArkRoxBot.Models;
using ArkRoxBot.Models.Config;
using ArkRoxBot.Services;

namespace ArkRoxBot.Services
{
    public class BackpackListingService
    {
        private readonly HttpClient _http;
        private readonly string _apiToken = "SAYD6yUbrDE1xSBOwW7pSspf1rtp6hI1NiSY/1EE8mM=";

        public BackpackListingService()
        {
            _http = new HttpClient();
        }

        public async Task ClearAllListingsAsync()
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Delete, "https://backpack.tf/api/classifieds/listings");
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", _apiToken);

            HttpResponseMessage response = await _http.SendAsync(request);
            Console.WriteLine(response.IsSuccessStatusCode
                ? "🧹 All listings cleared from Backpack.tf"
                : $"❌ Failed to clear listings: {response.StatusCode}");
        }

        public async Task PostBuyListingAsync(string itemName, decimal price, int quantity)
        {
            var body = new
            {
                item = itemName,
                intent = "buy",
                currencies = new { metal = price },
                details = $"Buying for {price} ref. Stock: 0 / {quantity}. Send an offer or add me and type !sell {itemName}",
                quantity = quantity
            };

            await PostListingAsync(body);
        }

        public async Task PostSellListingAsync(string itemName, decimal price, int quantity)
        {
            var body = new
            {
                item = itemName,
                intent = "sell",
                currencies = new { metal = price },
                details = $"Selling for {price} ref. Stock: {quantity} / 2 . Send an offer or add me and type !buy {itemName}",
                quantity = quantity
            };

            await PostListingAsync(body);
        }

        private async Task PostListingAsync(object listing)
        {
            string json = JsonSerializer.Serialize(listing);
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://backpack.tf/api/classifieds/listings")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Token", _apiToken);

            HttpResponseMessage response = await _http.SendAsync(request);
            Console.WriteLine(response.IsSuccessStatusCode
                ? "Listing posted successfully."
                : $"Failed to post listing: {response.StatusCode}");
        }

        public async Task RefreshListingsAsync(ConfigRoot config, PriceStore priceStore)
        {
            await ClearAllListingsAsync();

            foreach (BuyConfig item in config.BuyConfig)
            {
                PriceResult? price = priceStore.GetPrice(item.Name);
                if (price == null)
                {
                    Console.WriteLine($"Skipping {item.Name} (no price data)");
                    continue;
                }

                int currentOwned = 0; // Simulated inventory value

                if (currentOwned < item.MaxQuantity)
                {
                    await PostBuyListingAsync(item.Name, item.BuyPrice, item.MaxQuantity);
                }
                else
                {
                    Console.WriteLine($"Max quantity reached for {item.Name}, skipping buy listing");
                }
            }

            foreach (SellConfig item in config.SellConfig)
            {
                PriceResult? price = priceStore.GetPrice(item.Name);
                if (price == null)
                {
                    Console.WriteLine($"Skipping {item.Name} (no price data)");
                    continue;
                }

                if (item.Quantity > 0)
                {
                    await PostSellListingAsync(item.Name, item.SellPrice, item.Quantity);
                }
            }
        }

    }
}
