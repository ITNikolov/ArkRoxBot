using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ArkRoxBot.Helpers
{
    public static class ScrapUtils
    {
        private const decimal ScrapValue = 0.11m;
        private const decimal Tolerance = 0.0001m;

        public static bool IsValidScrapPrice(decimal price)
        {
            decimal remainder = price % ScrapValue;
            return Math.Abs(remainder) < Tolerance;
        }

        public static (int refined, int reclaimed, int scrap) BreakdownMetal(decimal price)
        {
            int totalScrap = (int)Math.Round(price / ScrapValue);
            int refined = totalScrap / 9;
            int leftover = totalScrap % 9;
            int reclaimed = leftover / 3;
            int scrap = leftover % 3;

            return (refined, reclaimed, scrap);
        }
    }
}
