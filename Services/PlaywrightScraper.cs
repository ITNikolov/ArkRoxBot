using Microsoft.Playwright;



namespace ArkRoxBot.Services
{
    public class PlaywrightScraper
    {
        private IBrowserContext? _context;

        private async Task ImportCookiesFromFileAsync(IBrowserContext context, string filePath)
        {
            var json = await File.ReadAllTextAsync(filePath);

            // parse as a dynamic array so bad fields do not break deserialization
            var rawCookies = System.Text.Json.JsonDocument.Parse(json).RootElement;

            var cookies = new List<Cookie>();

            foreach (var cookieJson in rawCookies.EnumerateArray())
            {
                cookies.Add(new Cookie
                {
                    Name = cookieJson.GetProperty("name").GetString(),
                    Value = cookieJson.GetProperty("value").GetString(),
                    Domain = cookieJson.GetProperty("domain").GetString(),
                    Path = cookieJson.TryGetProperty("path", out var p) ? p.GetString() : "/"
                    // we leave SameSite, Secure, etc. default
                });
            }

            await context.AddCookiesAsync(cookies);

            Console.WriteLine($"Imported {cookies.Count} cookies from {filePath}");
        }


        public async Task InitAsync()
        {
            var playwright = await Playwright.CreateAsync();

            _context = await playwright.Chromium.LaunchPersistentContextAsync(
                @"E:\ArkRox\arkrox-userdata",
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = false,
                    SlowMo = 50
                });

            await ImportCookiesFromFileAsync(_context, @"E:\ArkRox\arkrox-userdata\bp_cookies.json");
            await ImportCookiesFromFileAsync(_context, @"E:\ArkRox\arkrox-userdata\steam_cookies.json");

            await _context.AddCookiesAsync(new[]
{
                 new Cookie
                  {
                  Name = "cf_clearance",
                  Value = "J9Mtt4bpH_YXd3ejlRaF2.h5z87JaMQMxe.PCBNjSIo-1749210587-1.2.1.1-2J33Y9swVxDvxsMaXFkKGYqBCNBtbHN96hqA2IxDPKG1sBzJD6tZY2iBvPWTSaxB_TJ9yGP7MYbDvTrK48asQr_nwPmXl5CphPiKn3EUaWr0e8S_uOG2_cmlAV5IAvVyZN5ayh6DwwcdQkrA0EzOB7KoDIcyXsx8srCXMAOgkd6sPa4n587tOL05sO0HbuEG._bt9_TfsUVdU7wlnXPpR04AXcV86kkisurSEdE.9QJjmkiwfM_38IBM.I5Vf.gs3nLGCh1hrfyqhD7vzPcoQ6uGkfboVOUlu9q80AhsMFCMYiMZFdQSRE.bwuapAydiy17ThGfJJVBAS.uSH05x2xdN8Vvckwd29gHz.XqdNNE.IR61x4xPtWz_g1u1Te9X",
                  Domain = "backpack.tf",
                  Path = "/"
                  }
                    });

        }





        public async Task<string> FetchClassifiedsPageAsync(string itemName, int page = 1)
        {
            if (_context == null)
            {
                Console.WriteLine("Error: Browser not initialized.");
                return string.Empty;
            }

            var context = _context;
            var pageObj = await context.NewPageAsync();

            await pageObj.Context.AddCookiesAsync(new[]
{
    new Cookie
    {
        Name = "sessionid",
        Value = "b6e91a0a87a36c800349fa1b",
        Domain = "backpack.tf",
        Path = "/"
    },
    new Cookie
    {
        Name = "steamLoginSecure",
        Value = "76561199466477276%7C%7CeyAidHlwIjogIkpXVCIsICJhbGciOiAiRWREU0EiIH0.eyAiaXNzIjogInI6MDAwRl8yNjkxRUMyQl8zQjYxMCIsICJzdWIiOiAiNzY1NjExOTk0NjY0NzcyNzYiLCAiYXVkIjogWyAid2ViOmNvbW11bml0eSIgXSwgImV4cCI6IDE3NTE3OTgwMTgsICJuYmYiOiAxNzQzMDcwMTA2LCAiaWF0IjogMTc1MTcxMDEwNiwgImp0aSI6ICIwMDE4XzI2OTFFQzREXzMyOTgyIiwgIm9hdCI6IDE3NTE3MTAxMDYsICJydF9leHAiOiAxNzcwMDE2NDgwLCAicGVyIjogMCwgImlwX3N1YmplY3QiOiAiNzguMTI4LjY1LjE2MiIsICJpcF9jb25maXJtZXIiOiAiNzguMTI4LjY1LjE2MiIgfQ.bx8j3lrQ-3pQVJdf5MRrpByhDl8bXiFrZkl10u8NZVUtoM0DSRf_pdsXjstxlfpey5X1V5kdWDOGeY4KWjunDQ",
        Domain = "backpack.tf",
        Path = "/"
    },
    new Cookie
    {
        Name = "cf_clearance",
        Value = "J9Mtt4bpH_YXd3ejlRaF2.h5z87JaMQMxe.PCBNjSIo-1749210587-1.2.1.1-2J33Y9swVxDvxsMaXFkKGYqBCNBtbHN96hqA2IxDPKG1sBzJD6tZY2iBvPWTSaxB_TJ9yGP7MYbDvTrK48asQr_nwPmXl5CphPiKn3EUaWr0e8S_uOG2_cmlAV5IAvVyZN5ayh6DwwcdQkrA0EzOB7KoDIcyXsx8srCXMAOgkd6sPa4n587tOL05sO0HbuEG._bt9_TfsUVdU7wlnXPpR04AXcV86kkisurSEdE.9QJjmkiwfM_38IBM.I5Vf.gs3nLGCh1hrfyqhD7vzPcoQ6uGkfboVOUlu9q80AhsMFCMYiMZFdQSRE.bwuapAydiy17ThGfJJVBAS.uSH05x2xdN8Vvckwd29gHz.XqdNNE.IR61x4xPtWz_g1u1Te9X",
        Domain = "backpack.tf",
        Path = "/"
    }
});



            string urlName = Uri.EscapeDataString(itemName);
            string url = $"https://backpack.tf/classifieds?page={page}&item={urlName}&quality=6&tradable=1&craftable=1&australium=-1&killstreak_tier=0";

            await pageObj.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

            // wait for parent <ul class="media-list"> to be visible
            await Task.Delay(10000); // wait 10 seconds manually

            var listings = await pageObj.QuerySelectorAllAsync("li.listing");
            Console.WriteLine($"Found {listings.Count} listings on the page.");

            string content = await pageObj.ContentAsync();

            return content;
        }
    }
}
