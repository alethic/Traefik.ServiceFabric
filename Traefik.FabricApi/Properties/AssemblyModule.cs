using System.Fabric;

using Autofac;

using Cogito.Autofac;

namespace Traefik.FabricApi
{

    public class AssemblyModule : ModuleBase
    {

        protected override void Register(ContainerBuilder builder)
        {
            builder.RegisterFromAttributes(typeof(AssemblyModule).Assembly);
        }

    }

}
