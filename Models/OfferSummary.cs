using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ArkRoxBot.Models
{
    public sealed class OfferSummary
    {
        public Dictionary<string, int> ItemsToReceiveByName { get; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> ItemsToGiveByName { get; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

}
