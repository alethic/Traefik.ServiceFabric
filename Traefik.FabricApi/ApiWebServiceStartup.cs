using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using Cogito.Autofac;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Microsoft.ServiceFabric.Common.Security;

namespace Traefik.FabricApi
{

    [RegisterAs(typeof(ApiWebServiceStartup))]
    public class ApiWebServiceStartup
    {

        static readonly Uri apiBasePath = new Uri("http://localhost:19080/");

        readonly HttpClient client;
        readonly SecurityType securityType = SecurityType.Windows;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public ApiWebServiceStartup()
        {
            client = CreateHttpClient();
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
                case SecurityType.None:
                    break;
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
        /// Configures the application.
        /// </summary>
        /// <param name="app"></param>
        public void Configure(IApplicationBuilder app)
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
        }

        /// <summary>
        /// Returns <c>true</c> if the header is one that should be copied.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        bool ShouldCopyHeader(string name)
        {
            switch (name)
            {
                case "Host":
                    return false;
                default:
                    return true;
            }
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
            u.Query = context.Request.QueryString.Value.TrimStart('?');

            using (var request = new HttpRequestMessage())
            {
                request.Method = HttpMethod.Get;
                request.RequestUri = u.Uri;

                // copy incoming headers to API
                foreach (var h in context.Request.Headers)
                    if (ShouldCopyHeader(h.Key))
                        request.Headers.TryAddWithoutValidation(h.Key, h.Value.Select(i => i));

                // invoke request
                using (var response = await client.SendAsync(request))
                {
                    context.Response.StatusCode = (int)response.StatusCode;
                    context.Response.HttpContext.Features.Get<IHttpResponseFeature>().ReasonPhrase = response.ReasonPhrase;

                    // copy response headers from API
                    foreach (var h in response.Headers)
                        if (ShouldCopyHeader(h.Key))
                            context.Response.Headers.Add(h.Key, new StringValues(h.Value.ToArray()));

                    // copy content headers from API
                    foreach (var h in response.Content.Headers)
                        if (ShouldCopyHeader(h.Key))
                            context.Response.Headers.Add(h.Key, new StringValues(h.Value.ToArray()));

                    // copy body
                    await response.Content.CopyToAsync(context.Response.Body);
                }
            }
        }

    }

}
