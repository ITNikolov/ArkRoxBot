using HtmlAgilityPack;

namespace ArkRoxBot.Services
{
    public class HtmlClassifiedsScraper
    {
        private readonly HttpClient _http = new HttpClient();

        public HtmlClassifiedsScraper()
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
            );
        }


        public async Task<string> FetchClassifiedsPageAsync(string itemName, int page = 1)
        {
            string urlName = Uri.EscapeDataString(itemName);

            string url = $"https://backpack.tf/classifieds?page={page}&item={urlName}&quality=6&tradable=1&craftable=1&australium=-1&killstreak_tier=0";

            HttpResponseMessage response = await _http.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error: failed to get classifieds page for {itemName} on page {page}");
                return string.Empty;
            }

            string html = await response.Content.ReadAsStringAsync();

            return html;
        }
    }
}

