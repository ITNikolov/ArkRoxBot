using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ArkRoxBot.Interfaces
{
    public interface IPriceParser
    {
        bool TryParseToRefined(string priceText, out decimal refined);
    }
}
