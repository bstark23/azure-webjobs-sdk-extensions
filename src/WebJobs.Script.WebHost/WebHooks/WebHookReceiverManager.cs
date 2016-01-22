﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Controllers;
using Autofac;
using Autofac.Integration.WebApi;
using Microsoft.AspNet.WebHooks;
using Microsoft.AspNet.WebHooks.Config;
using Microsoft.AspNet.WebHooks.Diagnostics;
using Microsoft.Azure.WebJobs.Host;

namespace WebJobs.Script.WebHost.WebHooks
{
    /// <summary>
    /// Class managing routing of requests to registered WebHook Receivers. It initializes an
    /// <see cref="HttpConfiguration"/> and loads all registered WebHook Receivers.
    /// </summary>
    public class WebHookReceiverManager : IDisposable
    {
        internal const string AzureFunctionsCallbackKey = "MS_AzureFunctionsCallback";

        private readonly Dictionary<string, IWebHookReceiver> _receiverLookup;
        private readonly TraceWriter _trace;
        private HttpConfiguration _httpConfiguration;
        private bool disposedValue = false;

        public WebHookReceiverManager(SecretsManager secretsManager, TraceWriter trace)
        {
            _trace = trace;
            _httpConfiguration = new HttpConfiguration();

            var builder = new ContainerBuilder();
            ILogger logger = new WebHookLogger(_trace);
            builder.RegisterInstance<ILogger>(logger);
            builder.RegisterInstance<IWebHookHandler>(new DelegatingWebHookHandler());
            builder.RegisterInstance<IWebHookReceiverConfig>(new DynamicWebHookReceiverConfig(secretsManager));
            var container = builder.Build();

            WebHooksConfig.Initialize(_httpConfiguration);

            _httpConfiguration.DependencyResolver = new AutofacWebApiDependencyResolver(container);

            IEnumerable<IWebHookReceiver> receivers = _httpConfiguration.DependencyResolver.GetReceivers();
            _receiverLookup = receivers.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
        }

        public async Task<HttpResponseMessage> HandleRequestAsync(HttpFunctionInfo functionInfo, HttpRequestMessage request, Func<HttpRequestMessage, Task<HttpResponseMessage>> invokeFunction)
        {
            // First check if there is a registered WebHook Receiver for this request, and if
            // so use it
            IWebHookReceiver receiver = null;
            if (!functionInfo.IsWebHook || !_receiverLookup.TryGetValue(functionInfo.WebHookReceiver, out receiver))
            {
                // If the function is a not a correctly configured WebHook return 500
                return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
            }

            HttpRequestContext context = new HttpRequestContext
            {
                Configuration = _httpConfiguration
            };
            request.SetConfiguration(_httpConfiguration);

            // add the anonymous handler function from above to the request properties
            // so our custom WebHookHandler can invoke it at the right time
            request.Properties.Add(AzureFunctionsCallbackKey, invokeFunction);

            // TODO: Is there a better way? Requests content can't be read multiple
            // times, so this forces it to buffer
            await request.Content.ReadAsStringAsync();

            string receiverId = functionInfo.Function.Name.ToLowerInvariant();
            return await receiver.ReceiveAsync(receiverId, context, request);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    if (_httpConfiguration != null)
                    {
                        _httpConfiguration.Dispose();
                    }
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Custom <see cref="WebHookHandler"/> used to integrate ASP.NET WebHooks int our request pipeline.
        /// When a request is dispatched to a <see cref="WebHookReceiver"/>, after validating the request
        /// fully, it will delegate to this handler, allowing us to resume processing and dispatch the request
        /// to the function.
        /// </summary>
        private class DelegatingWebHookHandler : WebHookHandler
        {
            public override async Task ExecuteAsync(string receiver, WebHookHandlerContext context)
            {
                // At this point, the WebHookReceiver has validated this request, so we
                // now need to dispatch it to the actual function.

                // get the callback from request properties
                var requestHandler = (Func<HttpRequestMessage, Task<HttpResponseMessage>>)
                    context.Request.Properties[AzureFunctionsCallbackKey];

                // Invoke the function
                context.Response = await requestHandler(context.Request);
            }
        }
    }
}