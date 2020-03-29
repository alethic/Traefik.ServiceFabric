using System.Fabric;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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

        /// <summary>
        /// Periodically refreshes the Traefik dynamic configuration.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        protected override async Task RunLoopAsync(CancellationToken cancellationToken)
        {
            var yaml = await new TraefikYamlBuilder(Fabric).GetConfigurationAsync(cancellationToken);
            var path = Path.Combine(Context.CodePackageActivationContext.WorkDirectory, "conf", "fabric.yml");
            File.WriteAllText(path, yaml);
        }

    }

}
