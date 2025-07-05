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

            string sessionId = "e1b89e0d95f3ab4d3200db49";
            string steamLoginSecure = "76561199466477276%7C%7CeyAidHlwIjogIkpXVCIsICJhbGciOiAiRWREU0EiIH0.eyAiaXNzIjogInI6MDAwN18yNjg0QzlCM19EQzFDQyIsICJzdWIiOiAiNzY1NjExOTk0NjY0NzcyNzYiLCAiYXVkIjogWyAid2ViOmNvbW11bml0eSIgXSwgImV4cCI6IDE3NTE3MzY4NTcsICJuYmYiOiAxNzQzMDA4OTc2LCAiaWF0IjogMTc1MTY0ODk3NiwgImp0aSI6ICIwMDE4XzI2ODRDOUE5XzVCQTAxIiwgIm9hdCI6IDE3NTE2NDg5NzYsICJydF9leHAiOiAxNzY5NzYzMzI0LCAicGVyIjogMCwgImlwX3N1YmplY3QiOiAiNzguMTI4LjY1LjE2MiIsICJpcF9jb25maXJtZXIiOiAiNzguMTI4LjY1LjE2MiIgfQ.TBLPwwlwcYproiYCi22OhYKJpPg06ky6GnRv1QJS0kffWAt8pl-DOs3eqYeSnjUmqjFldlmeKOlCf5LzFVNaAQ";

            _http.DefaultRequestHeaders.Add("Cookie", $"sessionid={sessionId}; steamLoginSecure={steamLoginSecure}");
        }


        public async Task<string> FetchClassifiedsPageAsync(string itemName, int page = 1)
        {
            string urlName = Uri.EscapeDataString(itemName);

            string url = $"https://backpack.tf/classifieds?page={page}&item={urlName}&quality=6&tradable=1&craftable=1&australium=-1&killstreak_tier=0";

            HttpResponseMessage response = await _http.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error: failed to get classifieds page for {itemName} on page {page}");
                Console.WriteLine($"Status code: {response.StatusCode}");
                return string.Empty;
            }


            string html = await response.Content.ReadAsStringAsync();

            return html;
        }


    }
}

