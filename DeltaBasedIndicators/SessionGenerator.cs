using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TradingPlatform.BusinessLayer;

namespace DeltaBasedIndicators
{

    //📝 TODO: [Add logger]

    internal class SessionGenerator
    {
        public int baseUtcConverter{ get; set; }
        public CustomSession MyProperty { get; set; }
        public Session sess { get; set; }

        public SessionGenerator()
        {
        }
    }
}
