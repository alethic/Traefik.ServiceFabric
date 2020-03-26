using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Query;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using Autofac;

using Cogito.Collections;
using Cogito.ServiceFabric;
using Cogito.ServiceFabric.AspNetCore.Kestrel.Autofac;
using Cogito.ServiceFabric.Services.Autofac;
using Microsoft.ServiceFabric.Services.Client;
using Newtonsoft.Json.Linq;
using YamlDotNet.RepresentationModel;

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
            var toml = await GetConfigurationAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Gets the configuration settings to provide.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async Task<string> GetConfigurationAsync(CancellationToken cancellationToken)
        {
            var conf = new YamlMappingNode();

            // integrate configuration for each service
            await foreach (var app in GetApplicationAsync(cancellationToken))
                await foreach (var svc in GetServicesAsync(app, cancellationToken))
                    if (await GetServiceLabelsAsync(app, svc, cancellationToken) is IDictionary<string, string> l)
                        await AddServiceConfigurationAsync(app, svc, l, conf, cancellationToken);

            // serialize to string
            using var wrt = new StringWriter();
            new YamlStream(new YamlDocument(conf)).Save(wrt, false);
            return wrt.ToString();
        }

        /// <summary>
        /// Generates the TOML configuration values for the given service with the specified labels.
        /// </summary>
        /// <param name="app"></param>
        /// <param name="svc"></param>
        /// <param name="labels"></param>
        /// <param name="config"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async Task AddServiceConfigurationAsync(Application app, Service svc, IDictionary<string, string> labels, YamlMappingNode config, CancellationToken cancellationToken)
        {
            if (labels.GetOrDefault("traefik.enable") != "true")
                return;

            var rtrName = svc.ServiceName.ToString();
            var svcName = svc.ServiceName.ToString();

            var http = (YamlMappingNode)config.Children.GetOrDefault("http");
            if (http == null)
                config.Add("http", http = new YamlMappingNode());

            var routers = (YamlMappingNode)http.Children.GetOrDefault("routers");
            if (routers == null)
                http.Add("routers", routers = new YamlMappingNode());

            var router = (YamlMappingNode)routers.Children.GetOrDefault(rtrName);
            if (router == null)
                routers.Add(rtrName, router = new YamlMappingNode());

            var services = (YamlMappingNode)http.Children.GetOrDefault("services");
            if (services == null)
                http.Add("services", services = new YamlMappingNode());

            var service = (YamlMappingNode)services.Children.GetOrDefault(svcName);
            if (service == null)
                services.Add(svcName, service = new YamlMappingNode());

            foreach (var l in labels.Where(i => i.Key.StartsWith("traefik.http.router.")))
            {
                // router configuration property name
                var n = l.Key.Substring(20);

                // set router configuration
                if (router[n] == null)
                    router.Add(n, l.Value);
            }

            foreach (var l in labels.Where(i => i.Key.StartsWith("traefik.http.service.")))
            {
                // service configuration property name
                var n = l.Key.Substring(21);

                // set service configuration
                if (service[n] == null)
                    service.Add(n, l.Value);
            }

            var entryPoints = (YamlSequenceNode)router.Children.GetOrDefault("entryPoints");
            if (entryPoints == null)
                router.Add("entryPoints", entryPoints = new YamlSequenceNode());

            entryPoints.Add("http");

            var servers = (YamlSequenceNode)service.Children.GetOrDefault("servers");
            if (servers == null)
                service.Add("servers", servers = new YamlSequenceNode());

            // add each discovered server
            await foreach (var i in GetServiceServersAsync(app, svc, cancellationToken))
                servers.Add(i);
        }

        /// <summary>
        /// Gets the server endpoints assoicated with the service.
        /// </summary>
        /// <param name="app"></param>
        /// <param name="svc"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async IAsyncEnumerable<YamlNode> GetServiceServersAsync(Application app, Service svc, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var address in GetServiceServerEndpointAddressesAsync(app, svc, cancellationToken))
            {
                var n = new YamlMappingNode();
                n.Add("url", address);
                yield return n;
            }
        }

        /// <summary>
        /// Gets the server endpoints assoicated with the service.
        /// </summary>
        /// <param name="app"></param>
        /// <param name="svc"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async IAsyncEnumerable<string> GetServiceServerEndpointAddressesAsync(Application app, Service svc, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var resolver = ServicePartitionResolver.GetDefault();

            await foreach (var partition in GetPartitionsAsync(svc, cancellationToken))
                if (partition.PartitionInformation.Kind == ServicePartitionKind.Singleton)
                    if (await resolver.ResolveAsync(svc.ServiceName, ServicePartitionKey.Singleton, cancellationToken) is ResolvedServicePartition p)
                        foreach (var endpoint in p.Endpoints)
                            foreach (var address in JObject.Parse(endpoint.Address)["Endpoints"].ToArray())
                                yield return (string)address;
        }

        /// <summary>
        /// Gets the configuration settings for the specified service.
        /// </summary>
        /// <param name="app"></param>
        /// <param name="svc"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async Task<IDictionary<string, string>> GetServiceLabelsAsync(Application app, Service svc, CancellationToken cancellationToken)
        {
            if (svc.ServiceKind != ServiceKind.Stateless)
                return null;

            // obtain XML for the Traefik labels
            var labels = await GetTraefikLabelsAsync(app, svc, cancellationToken);
            if (labels == null)
                return null;

            return labels;
        }

        /// <summary>
        /// Gets the Traefik extension XML labels.
        /// </summary>
        /// <param name="app"></param>
        /// <param name="svc"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async Task<IDictionary<string, string>> GetTraefikLabelsAsync(Application app, Service svc, CancellationToken cancellationToken)
        {
            var xns = (XNamespace)"http://schemas.microsoft.com/2015/03/fabact-no-schema";
            var xml = await GetTraefikExtensionXmlAsync(app, svc, cancellationToken);
            if (xml == null)
                return null;

            return xml.Root.Elements(xns + "Label").ToDictionary(i => (string)i.Attribute("Key"), i => i.Value);
        }

        /// <summary>
        /// Gets the Traefik extension XML for the specified service.
        /// </summary>
        /// <param name="app"></param>
        /// <param name="svc"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async Task<XDocument> GetTraefikExtensionXmlAsync(Application app, Service svc, CancellationToken cancellationToken)
        {
            // find information about specific service type
            var serviceTypes = await Fabric.QueryManager.GetServiceTypeListAsync(app.ApplicationTypeName, app.ApplicationTypeVersion, svc.ServiceTypeName, TimeSpan.FromSeconds(5), cancellationToken);
            if (serviceTypes == null || serviceTypes.Count < 1)
                return null;

            var extension = serviceTypes[0].ServiceTypeDescription.Extensions.GetOrDefault("Traefik");
            if (extension == null)
                return null;

            var xml = XDocument.Parse(extension);
            return xml;
        }

        /// <summary>
        /// Iterates the available applications in the system.
        /// </summary>
        /// <returns></returns>
        async IAsyncEnumerable<Application> GetApplicationAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var apps = await Fabric.QueryManager.GetApplicationListAsync(null, TimeSpan.FromSeconds(5), cancellationToken);

            while (apps.Count > 0)
            {
                foreach (var app in apps)
                    yield return app;

                // we reached the end
                if (apps.ContinuationToken == null)
                    break;

                // retrieve next list of applications
                apps = await Fabric.QueryManager.GetApplicationListAsync(null, apps.ContinuationToken, TimeSpan.FromSeconds(5), cancellationToken);
            }
        }

        /// <summary>
        /// Iterates the available services in the system.
        /// </summary>
        /// <returns></returns>
        async IAsyncEnumerable<Service> GetServicesAsync(Application application, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var svcs = await Fabric.QueryManager.GetServiceListAsync(application.ApplicationName, null, TimeSpan.FromSeconds(5), cancellationToken);

            while (svcs.Count > 0)
            {
                foreach (var svc in svcs)
                    yield return svc;

                // we reached the end
                if (svcs.ContinuationToken == null)
                    break;

                // retrieve next list of services
                svcs = await Fabric.QueryManager.GetServiceListAsync(application.ApplicationName, null, svcs.ContinuationToken, TimeSpan.FromSeconds(5), cancellationToken);
            }
        }

        /// <summary>
        /// Iterates the available partitions for the service.
        /// </summary>
        /// <returns></returns>
        async IAsyncEnumerable<Partition> GetPartitionsAsync(Service service, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var parts = await Fabric.QueryManager.GetPartitionListAsync(service.ServiceName, null, TimeSpan.FromSeconds(5), cancellationToken);

            while (parts.Count > 0)
            {
                foreach (var svc in parts)
                    yield return svc;

                // we reached the end
                if (parts.ContinuationToken == null)
                    break;

                // retrieve next list of partitions
                parts = await Fabric.QueryManager.GetPartitionListAsync(service.ServiceName, null, parts.ContinuationToken, TimeSpan.FromSeconds(5), cancellationToken);
            }
        }

    }

}
