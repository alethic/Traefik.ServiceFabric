using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Query;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using Autofac;

using Cogito.Collections;
using Cogito.ServiceFabric;
using Cogito.ServiceFabric.AspNetCore.Kestrel.Autofac;
using Cogito.ServiceFabric.Services.Autofac;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.ServiceFabric.Services.Client;
using Newtonsoft.Json.Linq;
using YamlDotNet.Core.Tokens;
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
            var yaml = await GetConfigurationAsync(cancellationToken);
            var path = Path.Combine(Context.CodePackageActivationContext.WorkDirectory, "conf", "fabric.yml");
            File.WriteAllText(path, yaml);
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
        /// Generates a traefik compatible name from a service name.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        string NormalizeServiceName(Uri value)
        {
            return value.AbsolutePath.Replace("/", "_");
        }

        /// <summary>
        /// Generates the YAML configuration values for the given service with the specified labels.
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

            // find or create 'http' node
            var http = (YamlMappingNode)config.Children.GetOrDefault("http");
            if (http == null)
                config.Add("http", http = new YamlMappingNode());

            // find or create 'routers' node
            var routers = (YamlMappingNode)http.Children.GetOrDefault("routers");
            if (routers == null)
                http.Add("routers", routers = new YamlMappingNode());

            // apply routers
            foreach (var kvp in await GetServiceRouterNodesAsync(app, svc, labels, cancellationToken))
                routers.Add(kvp.Key, kvp.Value);

            // find or create 'services' node
            var services = (YamlMappingNode)http.Children.GetOrDefault("services");
            if (services == null)
                http.Add("services", services = new YamlMappingNode());

            // apply services
            foreach (var kvp in await GetServiceServiceNodesAsync(app, svc, labels, cancellationToken))
                services.Add(kvp.Key, kvp.Value);

            // apply default service to each router
            foreach (var router in routers.Children.Values.Cast<YamlMappingNode>())
                if (router.Children.GetOrDefault("service") == null)
                    router.Add("service", services.First().Key);
        }

        /// <summary>
        /// Generates the YAML configuration for the router.
        /// </summary>
        /// <param name="app"></param>
        /// <param name="svc"></param>
        /// <param name="labels"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async Task<IDictionary<string, YamlMappingNode>> GetServiceRouterNodesAsync(Application app, Service svc, IDictionary<string, string> labels, CancellationToken cancellationToken)
        {
            var routers = new Dictionary<string, YamlMappingNode>();

            foreach (var l in labels.Where(i => i.Key.StartsWith("traefik.http.router.")))
            {
                // split path
                var a = l.Key.Split('.');
                if (a.Length < 5)
                    continue;

                // get name of router
                var n = a[3];
                if (string.IsNullOrWhiteSpace(n))
                    continue;

                // apply to router
                var y = routers.GetOrAdd(NormalizeServiceName(svc.ServiceName) + "_" + n, _ => new YamlMappingNode());
                ApplyHttpRouterLabelPart(app, svc, y, string.Join(".", a[4..]), l.Value);
            }

            // add default router if not specified
            if (routers.Count == 0)
                routers.Add(NormalizeServiceName(svc.ServiceName), new YamlMappingNode());

            // add default values to routers
            foreach (var router in routers.Values)
            {
                // add default entry point if none specified
                var entryPoints = (YamlSequenceNode)router.Children.GetOrDefault("entryPoints");
                if (entryPoints == null)
                    router.Add("entryPoints", entryPoints = new YamlSequenceNode("http"));

                // add default rule if none specified
                if (router.Children.GetOrDefault("rule") == null)
                    router.Add("rule", "Host(`*`)");
            }

            return routers;
        }

        /// <summary>
        /// Applies the given path based label information to the router node.
        /// </summary>
        /// <param name="app"></param>
        /// <param name="svc"></param>
        /// <param name="yaml"></param>
        /// <param name="part"></param>
        /// <param name="value"></param>
        void ApplyHttpRouterLabelPart(Application app, Service svc, YamlMappingNode yaml, string part, string value)
        {
            switch (part)
            {
                case "priority":
                case "rule":
                case "service":
                case "tls":
                case "tls.certresolver":
                case "tls.options":
                    yaml.Add(part, new YamlScalarNode(value));
                    break;
                case "entrypoints":
                case "middlewares":
                    var entryPoints = value.Split(",", StringSplitOptions.RemoveEmptyEntries).Select(i => NormalizeServiceName(svc.ServiceName) + "_" + i.Trim() + "@file").ToArray();
                    yaml.Add("entrypoints", new YamlSequenceNode(entryPoints));
                    break;
            }
        }

        /// <summary>
        /// Generates the YAML configuration for the service.
        /// </summary>
        /// <param name="app"></param>
        /// <param name="svc"></param>
        /// <param name="labels"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async Task<IDictionary<string, YamlMappingNode>> GetServiceServiceNodesAsync(Application app, Service svc, IDictionary<string, string> labels, CancellationToken cancellationToken)
        {
            var services = new Dictionary<string, YamlMappingNode>();

            foreach (var l in labels.Where(i => i.Key.StartsWith("traefik.http.service.")))
            {
                // split path
                var a = l.Key.Split('.');
                if (a.Length < 5)
                    continue;

                // get name of router
                var n = a[3];
                if (string.IsNullOrWhiteSpace(n))
                    continue;

                // apply to router
                var y = services.GetOrAdd(NormalizeServiceName(svc.ServiceName) + "_" + n, _ => new YamlMappingNode());
                ApplyHttpServiceLabelPart(y, string.Join(".", a[4..]), l.Value);
            }

            // add default service if not specified
            if (services.Count == 0)
                services.Add(NormalizeServiceName(svc.ServiceName), new YamlMappingNode());

            // apply missing values for all services
            foreach (var service in services.Values)
            {
                // get or create load balancer section
                var loadBalancer = (YamlMappingNode)service.Children.GetOrDefault("loadBalancer");
                if (loadBalancer == null)
                    service.Add("loadBalancer", loadBalancer = new YamlMappingNode());

                // get or create servers section
                var servers = (YamlSequenceNode)loadBalancer.Children.GetOrDefault("servers");
                if (servers == null)
                    loadBalancer.Add("servers", servers = new YamlSequenceNode());

                // add servers if none specified
                if (servers.Children.Count == 0)
                {
                    // identify the configured endpoint name
                    var serviceFabric = (YamlMappingNode)service.Children.GetOrDefault("servicefabric");
                    var endpoint = serviceFabric != null ? (string)(YamlScalarNode)serviceFabric.Children.GetOrDefault("endpoint") : null;

                    // get the backend servers for the endpoint
                    await foreach (var i in GetServiceServerNodesAsync(app, svc, endpoint, cancellationToken))
                        servers.Add(i);
                }
            }

            return services;
        }

        /// <summary>
        /// Applies the given path based label information to the service node.
        /// </summary>
        /// <param name="yaml"></param>
        /// <param name="part"></param>
        /// <param name="value"></param>
        void ApplyHttpServiceLabelPart(YamlMappingNode yaml, string part, string value)
        {
            switch (part)
            {
                case "loadbalancer.healthcheck.followredirects":
                case "loadbalancer.healthcheck.headers.name0":
                case "loadbalancer.healthcheck.hostname":
                case "loadbalancer.healthcheck.interval":
                case "loadbalancer.healthcheck.path":
                case "loadbalancer.healthcheck.port":
                case "loadbalancer.healthcheck.scheme":
                case "loadbalancer.healthcheck.timeout":
                case "loadbalancer.passhostheader":
                case "loadbalancer.responseforwarding.flushinterval":
                case "loadbalancer.sticky":
                case "loadbalancer.sticky.cookie.httponly":
                case "loadbalancer.sticky.cookie.name":
                case "loadbalancer.sticky.cookie.secure":
                case "loadbalancer.sticky.cookie.samesite":
                case "loadbalancer.server.port":
                case "loadbalancer.server.scheme":
                    yaml.Add(part, new YamlScalarNode(value));
                    break;
            }
        }

        /// <summary>
        /// Gets the server endpoints assoicated with the service.
        /// </summary>
        /// <param name="app"></param>
        /// <param name="svc"></param>
        /// <param name="endpoint"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async IAsyncEnumerable<YamlNode> GetServiceServerNodesAsync(Application app, Service svc, string endpoint, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var address in GetServiceServerEndpointAddressesAsync(app, svc, endpoint, cancellationToken))
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
        /// <param name="endpoint"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async IAsyncEnumerable<string> GetServiceServerEndpointAddressesAsync(Application app, Service svc, string endpointName, [EnumeratorCancellation] CancellationToken cancellationToken)
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

            var labels = new Dictionary<string, string>();

            // obtain traefik labels from manifest extension
            await foreach (var kvp in GetTraefikExtensionLabelsAsync(app, svc, cancellationToken))
                labels[kvp.Item1] = kvp.Item2;

            // obtain traefik labels from service properties
            await foreach (var kvp in GetTraefikPropertyLabelsAsync(app, svc, cancellationToken))
                labels[kvp.Item1] = kvp.Item2;

            return labels;
        }

        /// <summary>
        /// Gets the Traefik service properties.
        /// </summary>
        /// <param name="app"></param>
        /// <param name="svc"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async IAsyncEnumerable<(string, string)> GetTraefikPropertyLabelsAsync(Application app, Service svc, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var properties = await Fabric.PropertyManager.EnumeratePropertiesAsync(svc.ServiceName, true, null, TimeSpan.FromSeconds(5), cancellationToken);

            while (properties.Count > 0)
            {
                foreach (var property in properties)
                    if (property.Metadata.TypeId == PropertyTypeId.String)
                        if (property.GetValue<string>() is string value && value.StartsWith("traefik."))
                            yield return (property.Metadata.PropertyName, value);

                // we reached the end
                if (properties.HasMoreData == false)
                    break;

                // retrieve next list of properties
                properties = await Fabric.PropertyManager.EnumeratePropertiesAsync(svc.ServiceName, true, properties, TimeSpan.FromSeconds(5), cancellationToken);
            }
        }

        /// <summary>
        /// Gets the Traefik extension XML labels.
        /// </summary>
        /// <param name="app"></param>
        /// <param name="svc"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async IAsyncEnumerable<(string, string)> GetTraefikExtensionLabelsAsync(Application app, Service svc, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var xns = (XNamespace)"http://schemas.microsoft.com/2015/03/fabact-no-schema";
            var xml = await GetTraefikExtensionXmlAsync(app, svc, cancellationToken);
            if (xml == null)
                yield break;

            foreach (var kvp in xml.Root.Elements(xns + "Label"))
                yield return ((string)kvp.Attribute("Key"), kvp.Value);
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
