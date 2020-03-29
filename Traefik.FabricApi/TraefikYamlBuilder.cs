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

using Microsoft.ServiceFabric.Services.Client;

using Newtonsoft.Json.Linq;

using YamlDotNet.RepresentationModel;

namespace Traefik.FabricApi
{

    /// <summary>
    /// Builds the Traefik YAML from access to Service Fabric.
    /// </summary>
    public class TraefikYamlBuilder
    {

        readonly FabricClient fabric;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="fabric"></param>
        public TraefikYamlBuilder(FabricClient fabric)
        {
            this.fabric = fabric ?? throw new ArgumentNullException(nameof(fabric));
        }

        /// <summary>
        /// Gets the configuration settings to provide.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<string> GetConfigurationAsync(CancellationToken cancellationToken)
        {
            var conf = new YamlMappingNode();

            // integrate configuration for each service
            await foreach (var app in GetApplicationAsync(cancellationToken))
                await foreach (var svc in GetServicesAsync(app, cancellationToken))
                    if (await GetServiceLabelsAsync(app, svc, cancellationToken) is IDictionary<string, string> l)
                        await AddHttpConfigurationAsync(app, svc, l, conf, cancellationToken);

            // serialize to string
            using var wrt = new StringWriter();
            new YamlStream(new YamlDocument(conf)).Save(wrt, false);
            return wrt.ToString();
        }

        /// <summary>
        /// Generates a Traefik compatible name from a service.
        /// </summary>
        /// <param name="service"></param>
        /// <returns></returns>
        string ToTraefikName(Service service, string suffix)
        {
            return service.ServiceName.ToString() + "_" + suffix;
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
        async Task AddConfigurationAsync(Application app, Service svc, IDictionary<string, string> labels, YamlMappingNode config, CancellationToken cancellationToken)
        {
            await AddHttpConfigurationAsync(app, svc, labels, config, cancellationToken);
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
        async Task AddHttpConfigurationAsync(Application app, Service svc, IDictionary<string, string> labels, YamlMappingNode config, CancellationToken cancellationToken)
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
            await foreach (var kvp in GetHttpRouterNodesAsync(app, svc, labels, cancellationToken))
                routers.Add(kvp.Item1, kvp.Item2);

            // find or create 'services' node
            var services = (YamlMappingNode)http.Children.GetOrDefault("services");
            if (services == null)
                http.Add("services", services = new YamlMappingNode());

            // apply services
            await foreach (var kvp in GetHttpServiceNodesAsync(app, svc, labels, cancellationToken))
                services.Add(kvp.Item1, kvp.Item2);

            var middlewares = (YamlMappingNode)http.Children.GetOrDefault("middlewares");
            if (middlewares == null)
                http.Add("middlewares", middlewares = new YamlMappingNode());

            // apply middlewares
            await foreach (var kvp in GetHttpMiddlewaresNodesAsync(app, svc, labels, cancellationToken))
                middlewares.Add(kvp.Item1, kvp.Item2);

            // apply default service to each router
            if (services.Children.Count > 0)
                foreach (YamlMappingNode router in routers.Children.Values)
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
        async IAsyncEnumerable<(string, YamlMappingNode)> GetHttpRouterNodesAsync(Application app, Service svc, IDictionary<string, string> labels, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var routers = new Dictionary<string, YamlMappingNode>();

            foreach (var l in labels.Where(i => i.Key.StartsWith("traefik.http.routers.")))
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
                var y = routers.GetOrAdd(ToTraefikName(svc, n), _ => new YamlMappingNode());
                ApplyHttpRouterLabelPart(app, svc, y, a[4..], l.Value);
            }

            // add default router if not specified
            if (routers.Count == 0)
                routers.Add(ToTraefikName(svc, "default"), new YamlMappingNode());

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

            // iterate out routers
            foreach (var kvp in routers)
                yield return (kvp.Key, kvp.Value);
        }

        /// <summary>
        /// Applies the given path based label information to the router node.
        /// </summary>
        /// <param name="app"></param>
        /// <param name="svc"></param>
        /// <param name="yaml"></param>
        /// <param name="parts"></param>
        /// <param name="value"></param>
        void ApplyHttpRouterLabelPart(Application app, Service svc, YamlMappingNode yaml, ArraySegment<string> parts, string value)
        {
            switch (parts[0].ToLower())
            {
                case "priority":
                case "rule":
                    yaml.Add(parts[0], new YamlScalarNode(value));
                    break;
                case "tls":
                    ApplyHttpRouterTlsLabelPart((YamlMappingNode)yaml.Children.GetOrAdd("tls", _ => new YamlMappingNode()), parts[1..], value);
                    break;
                case "middlewares":
                    var entryPoints = value.Split(",", StringSplitOptions.RemoveEmptyEntries).Select(i => ToTraefikName(svc, i.Trim()) + "@file").ToArray();
                    yaml.Add("entrypoints", new YamlSequenceNode(entryPoints));
                    break;
            }
        }

        /// <summary>
        /// Applies the given path based label information to the router node.
        /// </summary>
        /// <param name="yaml"></param>
        /// <param name="parts"></param>
        /// <param name="value"></param>
        void ApplyHttpRouterTlsLabelPart(YamlMappingNode yaml, ArraySegment<string> parts, string value)
        {
            switch (parts[0].ToLower())
            {
                case "certresolver":
                case "options":
                    yaml.Add(parts[0], new YamlScalarNode(value));
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
        async IAsyncEnumerable<(string, YamlMappingNode)> GetHttpServiceNodesAsync(Application app, Service svc, IDictionary<string, string> labels, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var config = new Dictionary<string, YamlMappingNode>();

            foreach (var l in labels.Where(i => i.Key.StartsWith("traefik.http.services.")))
            {
                // split path
                var a = l.Key.Split('.');
                if (a.Length < 5)
                    continue;

                // get name of service
                var n = a[3];
                if (string.IsNullOrWhiteSpace(n))
                    continue;

                // apply to service
                var y = config.GetOrAdd(ToTraefikName(svc, n), _ => new YamlMappingNode());
                ApplyHttpServiceLabelPart(y, a[4..], l.Value);
            }

            // add default service if not specified
            if (config.Count == 0)
                config.Add(ToTraefikName(svc, "default"), new YamlMappingNode());

            // apply missing values for all services
            foreach (var kvp in config)
            {
                // get or create load balancer section
                var loadBalancer = (YamlMappingNode)kvp.Value.Children.GetOrDefault("loadBalancer");
                if (loadBalancer == null)
                    kvp.Value.Add("loadBalancer", loadBalancer = new YamlMappingNode());

                // get or create servers section
                var servers = (YamlSequenceNode)loadBalancer.Children.GetOrDefault("servers");
                if (servers == null)
                    loadBalancer.Add("servers", servers = new YamlSequenceNode());

                // add servers if none specified
                if (servers.Children.Count == 0)
                {
                    // identify the configured endpoint name
                    var serviceFabric = (YamlMappingNode)kvp.Value.Children.GetOrDefault("servicefabric");
                    var endpoint = serviceFabric != null ? (string)(YamlScalarNode)serviceFabric.Children.GetOrDefault("endpoint") : null;

                    // get the backend servers for the endpoint
                    await foreach (var i in GetHttpServiceServerNodesAsync(app, svc, endpoint, cancellationToken))
                        servers.Add(i);
                }

                // emit configured services
                if (servers.Children.Count > 0)
                    yield return (kvp.Key, kvp.Value);
            }
        }

        /// <summary>
        /// Applies the given path based label information to the service node.
        /// </summary>
        /// <param name="yaml"></param>
        /// <param name="parts"></param>
        /// <param name="value"></param>
        void ApplyHttpServiceLabelPart(YamlMappingNode yaml, ArraySegment<string> parts, string value)
        {
            switch (parts[0].ToLower())
            {
                case "servicefabric":
                    ApplyHttpServiceFabricLabelPart((YamlMappingNode)yaml.Children.GetOrAdd("servicefabric", _ => new YamlMappingNode()), parts[1..], value);
                    break;
                case "loadbalancer":
                    ApplyHttpServiceLoadBalancerLabelPart((YamlMappingNode)yaml.Children.GetOrAdd("loadBalancer", _ => new YamlMappingNode()), parts[1..], value);
                    break;
            }
        }

        /// <summary>
        /// Applies the given path based label information to the service node.
        /// </summary>
        /// <param name="yaml"></param>
        /// <param name="parts"></param>
        /// <param name="value"></param>
        void ApplyHttpServiceFabricLabelPart(YamlMappingNode yaml, ArraySegment<string> parts, string value)
        {
            switch (parts[0].ToLower())
            {
                case "endpoint":
                    yaml.Add(parts[0], new YamlScalarNode(value));
                    break;
            }
        }

        /// <summary>
        /// Applies the given path based label information to the service node.
        /// </summary>
        /// <param name="yaml"></param>
        /// <param name="parts"></param>
        /// <param name="value"></param>
        void ApplyHttpServiceLoadBalancerLabelPart(YamlMappingNode yaml, ArraySegment<string> parts, string value)
        {
            switch (parts[0])
            {
                case "healthcheck":
                    ApplyHttpServiceLoadBalancerHealthCheckLabelPart((YamlMappingNode)yaml.Children.GetOrAdd("healthCheck", _ => new YamlMappingNode()), parts[1..], value);
                    break;
                case "responseforwarding":
                    ApplyHttpServiceLoadBalancerResponseForwardingLabelPart((YamlMappingNode)yaml.Children.GetOrAdd("responseForwarding", _ => new YamlMappingNode()), parts[1..], value);
                    break;
                case "sticky":
                    ApplyHttpServiceLoadBalancerStickyLabelPart((YamlMappingNode)yaml.Children.GetOrAdd("sticky", _ => new YamlMappingNode()), parts[1..], value);
                    break;
                case "passhostheader":
                    yaml.Add(parts[0], new YamlScalarNode(value));
                    break;
            }
        }

        /// <summary>
        /// Applies the given path based label information to the service node.
        /// </summary>
        /// <param name="yaml"></param>
        /// <param name="parts"></param>
        /// <param name="value"></param>
        void ApplyHttpServiceLoadBalancerHealthCheckLabelPart(YamlMappingNode yaml, ArraySegment<string> parts, string value)
        {
            switch (parts[0])
            {
                case "headers":
                    ApplyHttpServiceLoadBalancerHealthCheckHeadersLabelPart((YamlMappingNode)yaml.Children.GetOrAdd("headers", _ => new YamlMappingNode()), parts[1..], value);
                    break;
                case "followredirects":
                case "hostname":
                case "interval":
                case "path":
                case "port":
                case "scheme":
                case "timeout":
                    yaml.Add(parts[0], new YamlScalarNode(value));
                    break;
            }
        }

        /// <summary>
        /// Applies the given path based label information to the service node.
        /// </summary>
        /// <param name="yaml"></param>
        /// <param name="parts"></param>
        /// <param name="value"></param>
        void ApplyHttpServiceLoadBalancerHealthCheckHeadersLabelPart(YamlMappingNode yaml, ArraySegment<string> parts, string value)
        {
            yaml.Add(parts[0], new YamlScalarNode(value));
        }

        /// <summary>
        /// Applies the given path based label information to the service node.
        /// </summary>
        /// <param name="yaml"></param>
        /// <param name="parts"></param>
        /// <param name="value"></param>
        void ApplyHttpServiceLoadBalancerResponseForwardingLabelPart(YamlMappingNode yaml, ArraySegment<string> parts, string value)
        {
            switch (parts[0])
            {
                case "flushinterval":
                    yaml.Add(parts[0], new YamlScalarNode(value));
                    break;
            }
        }

        /// <summary>
        /// Applies the given path based label information to the service node.
        /// </summary>
        /// <param name="yaml"></param>
        /// <param name="parts"></param>
        /// <param name="value"></param>
        void ApplyHttpServiceLoadBalancerStickyLabelPart(YamlMappingNode yaml, ArraySegment<string> parts, string value)
        {
            switch (parts[0])
            {
                case "cookie":
                    ApplyHttpServiceLoadBalancerStickyCookieLabelPart((YamlMappingNode)yaml.Children.GetOrAdd("cookie", _ => new YamlMappingNode()), parts[1..], value);
                    break;
            }
        }

        /// <summary>
        /// Applies the given path based label information to the service node.
        /// </summary>
        /// <param name="yaml"></param>
        /// <param name="parts"></param>
        /// <param name="value"></param>
        void ApplyHttpServiceLoadBalancerStickyCookieLabelPart(YamlMappingNode yaml, ArraySegment<string> parts, string value)
        {
            switch (parts[0])
            {
                case "httponly":
                case "name":
                case "secure":
                case "samesite":
                    yaml.Add(parts[0], new YamlScalarNode(value));
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
        async IAsyncEnumerable<YamlNode> GetHttpServiceServerNodesAsync(Application app, Service svc, string endpoint, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var address in GetServiceServerEndpointAddressesAsync(app, svc, endpoint, cancellationToken))
            {
                if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
                    if (uri.Scheme == Uri.UriSchemeHttp ||
                        uri.Scheme == Uri.UriSchemeHttps)
                    {
                        var n = new YamlMappingNode();
                        n.Add("url", address);
                        yield return n;
                    }
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
        async IAsyncEnumerable<(string, YamlMappingNode)> GetHttpMiddlewaresNodesAsync(Application app, Service svc, IDictionary<string, string> labels, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield break;
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
                    await foreach (var replica in GetReplicasAsync(partition, cancellationToken))
                        if (JObject.Parse(replica.ReplicaAddress)["Endpoints"] is JObject endpoints)
                            yield return endpointName != null ? (string)endpoints[endpointName] : (string)endpoints.First;
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
            var properties = await fabric.PropertyManager.EnumeratePropertiesAsync(svc.ServiceName, true, null, TimeSpan.FromSeconds(5), cancellationToken);

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
                properties = await fabric.PropertyManager.EnumeratePropertiesAsync(svc.ServiceName, true, properties, TimeSpan.FromSeconds(5), cancellationToken);
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
            var serviceTypes = await fabric.QueryManager.GetServiceTypeListAsync(app.ApplicationTypeName, app.ApplicationTypeVersion, svc.ServiceTypeName, TimeSpan.FromSeconds(5), cancellationToken);
            if (serviceTypes == null || serviceTypes.Count < 1)
                return null;

            var extension = serviceTypes[0].ServiceTypeDescription.Extensions.GetOrDefault("Traefik2");
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
            var apps = await fabric.QueryManager.GetApplicationListAsync(null, TimeSpan.FromSeconds(5), cancellationToken);

            while (apps.Count > 0)
            {
                foreach (var app in apps)
                    yield return app;

                // we reached the end
                if (apps.ContinuationToken == null)
                    break;

                // retrieve next list of applications
                apps = await fabric.QueryManager.GetApplicationListAsync(null, apps.ContinuationToken, TimeSpan.FromSeconds(5), cancellationToken);
            }
        }

        /// <summary>
        /// Iterates the available services in the system.
        /// </summary>
        /// <returns></returns>
        async IAsyncEnumerable<Service> GetServicesAsync(Application application, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var svcs = await fabric.QueryManager.GetServiceListAsync(application.ApplicationName, null, TimeSpan.FromSeconds(5), cancellationToken);

            while (svcs.Count > 0)
            {
                foreach (var svc in svcs)
                    yield return svc;

                // we reached the end
                if (svcs.ContinuationToken == null)
                    break;

                // retrieve next list of services
                svcs = await fabric.QueryManager.GetServiceListAsync(application.ApplicationName, null, svcs.ContinuationToken, TimeSpan.FromSeconds(5), cancellationToken);
            }
        }

        /// <summary>
        /// Iterates the available partitions for the service.
        /// </summary>
        /// <returns></returns>
        async IAsyncEnumerable<Partition> GetPartitionsAsync(Service service, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var parts = await fabric.QueryManager.GetPartitionListAsync(service.ServiceName, null, TimeSpan.FromSeconds(5), cancellationToken);

            while (parts.Count > 0)
            {
                foreach (var svc in parts)
                    yield return svc;

                // we reached the end
                if (parts.ContinuationToken == null)
                    break;

                // retrieve next list of partitions
                parts = await fabric.QueryManager.GetPartitionListAsync(service.ServiceName, null, parts.ContinuationToken, TimeSpan.FromSeconds(5), cancellationToken);
            }
        }

        /// <summary>
        /// Iterates the available replicas for the partition.
        /// </summary>
        /// <returns></returns>
        async IAsyncEnumerable<Replica> GetReplicasAsync(Partition partition, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var replicas = await fabric.QueryManager.GetReplicaListAsync(partition.PartitionInformation.Id, null, TimeSpan.FromSeconds(5), cancellationToken);

            while (replicas.Count > 0)
            {
                foreach (var replica in replicas)
                    yield return replica;

                // we reached the end
                if (replicas.ContinuationToken == null)
                    break;

                // retrieve next list of replicas
                replicas = await fabric.QueryManager.GetReplicaListAsync(partition.PartitionInformation.Id, replicas.ContinuationToken, TimeSpan.FromSeconds(5), cancellationToken);
            }
        }

    }

}
