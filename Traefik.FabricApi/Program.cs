using System.Threading;
using System.Threading.Tasks;

using Autofac;
using Autofac.Integration.ServiceFabric;

using Cogito.Autofac;
using Cogito.ServiceFabric.AspNetCore.Kestrel.Autofac;

namespace Traefik.FabricApi
{

    public static class Program
    {

        /// <summary>
        /// Main application entry point.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public static async Task Main(string[] args)
        {
            var builder = new ContainerBuilder();
            builder.RegisterAllAssemblyModules();
            builder.RegisterServiceFabricSupport();
            builder.RegisterStatelessKestrelWebService<Startup>("Traefik.FabricApi", "ServiceEndpoint");

            using (builder.Build())
                await Task.Delay(Timeout.Infinite);
        }

    }

}
