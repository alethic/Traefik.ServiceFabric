using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using Autofac;
using Autofac.Extensions.DependencyInjection;

using Cogito.Autofac;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Microsoft.ServiceFabric.Common.Security;

namespace Traefik.FabricApi
{

    [RegisterAs(typeof(Startup))]
    public class Startup
    {

        static readonly Uri apiBasePath = new Uri("http://localhost:19080/");

        readonly ILifetimeScope parent;
        readonly HttpClient client;
        readonly SecurityType securityType;

        ILifetimeScope scope;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="parent"></param>
        public Startup(ILifetimeScope parent)
        {
            this.parent = parent ?? throw new ArgumentNullException(nameof(parent));
            this.client = CreateHttpClient();
        }

        /// <summary>
        /// Creates a <see cref="HttpClient"/> configured to proxy the Service Fabric API.
        /// </summary>
        /// <returns></returns>
        HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler();

            switch (securityType)
            {
                case SecurityType.Claims:
                    throw new NotSupportedException();
                case SecurityType.X509:
                    throw new NotSupportedException();
                case SecurityType.Windows:
                    handler.UseDefaultCredentials = true;
                    break;
            }

            return new HttpClient(handler);
        }

        /// <summary>
        /// Registers framework dependencies.
        /// </summary>
        /// <param name="services"></param>
        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
            return new AutofacServiceProvider(scope = parent.BeginLifetimeScope(builder => builder.Populate(services)));
        }

        /// <summary>
        /// Configures the application.
        /// </summary>
        /// <param name="app"></param>
        /// <param name="applicationLifetime"></param>
        public void Configure(IApplicationBuilder app, IApplicationLifetime applicationLifetime)
        {
            app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Method == HttpMethods.Get)
                {
                    await ForwardGetRequest(ctx);
                    return;
                }

                await next();
            });

            applicationLifetime.ApplicationStopped.Register(() => scope.Dispose());
        }

        /// <summary>
        /// Forwards a GET request.
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        async Task ForwardGetRequest(HttpContext context)
        {
            var u = new UriBuilder(apiBasePath);
            u.Path = context.Request.PathBase + context.Request.Path;
            u.Query = context.Request.QueryString.ToUriComponent();

            using (var request = new HttpRequestMessage())
            {
                request.Method = HttpMethod.Get;
                request.RequestUri = u.Uri;

                // copy incoming headers to API
                foreach (var h in context.Request.Headers)
                    request.Headers.TryAddWithoutValidation(h.Key, h.Value.Select(i => i));

                // reset host header
                request.Headers.Host = u.Host + ":" + u.Port;

                // invoke request
                using (var response = await client.SendAsync(request))
                {
                    context.Response.StatusCode = (int)response.StatusCode;
                    context.Response.HttpContext.Features.Get<IHttpResponseFeature>().ReasonPhrase = response.ReasonPhrase;

                    // copy response headers from API
                    foreach (var h in response.Headers)
                        context.Response.Headers.Add(h.Key, new StringValues(h.Value.ToArray()));

                    // copy body
                    await response.Content.CopyToAsync(context.Response.Body);
                }
            }
        }

    }

}
