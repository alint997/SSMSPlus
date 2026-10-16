using SSMSPlusCore.Integration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Demo.Services
{
    class DemoServiceCacheIntegration : IServiceCacheIntegration
    {
        void IServiceCacheIntegration.OpenScriptInNewWindow(string script)
        {
            return;
        }

        void IServiceCacheIntegration.OpenScriptInNewWindow(string script, string server, string database)
        {
            return;
        }
    }
}
