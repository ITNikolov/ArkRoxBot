using System;
using System.Collections.Generic;

namespace ArkRoxBot.Services
{
    public sealed class OfferSummary
    {
        public Dictionary<string, int> ItemsToReceiveByName { get; } =
            new Dictionary<string, int>(StringComparer.Ordinal);

        public Dictionary<string, int> ItemsToGiveByName { get; } =
            new Dictionary<string, int>(StringComparer.Ordinal);
    }
}
