using System.Fabric;

using Autofac;

using Cogito.ServiceFabric;
using Cogito.ServiceFabric.AspNetCore.Kestrel.Autofac;
using Cogito.ServiceFabric.Services.Autofac;

namespace Traefik.FabricApi
{

    [RegisterStatelessService("Traefik.FabricApi", DefaultEndpointName = "ServiceEndpoint")]
    public class ApiWebService : StatelessKestrelWebService<ApiWebServiceStartup>
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="scope"></param>
        /// <param name="endpoint"></param>
        public ApiWebService(StatelessServiceContext context, ILifetimeScope scope, DefaultServiceEndpoint endpoint = null) :
            base(context, scope, endpoint)
        {

        }

    }

}
