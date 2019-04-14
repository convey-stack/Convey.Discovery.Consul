using System;
using Consul;
using Convey.Discovery.Consul.Builders;
using Convey.Discovery.Consul.Http;
using Convey.Discovery.Consul.Registries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Convey.Discovery.Consul
{
    public static class Extensions
    {
        private const string SectionName = "consul";
        private const string RegistryName = "discovery.consul";

        public static IConveyBuilder AddConsul(this IConveyBuilder builder, string sectionName = SectionName)
        {
            var options = builder.GetOptions<ConsulOptions>(SectionName);
            return builder.AddConsul(options);
        }
        
        public static IConveyBuilder AddConsul(this IConveyBuilder builder, Func<IConsulOptionsBuilder, IConsulOptionsBuilder> buildOptions)
        {
            var options = buildOptions(new ConsulOptionsBuilder()).Build();
            return builder.AddConsul(options);
        }

        public static IConveyBuilder AddConsul(this IConveyBuilder builder, ConsulOptions options)
        {
            if (!options.Enabled || !builder.TryRegister(RegistryName))
            {
                return builder;
            }

            builder.Services.AddSingleton(options);
            builder.Services.AddTransient<IConsulServicesRegistry, ConsulServicesRegistry>();
            builder.Services.AddTransient<ConsulServiceDiscoveryMessageHandler>();
            builder.Services.AddHttpClient<IConsulHttpClient, ConsulHttpClient>()
                .AddHttpMessageHandler<ConsulServiceDiscoveryMessageHandler>();

            builder.Services.AddSingleton<IConsulClient>(c => new ConsulClient(cfg =>
            {
                if (!string.IsNullOrEmpty(options.Url))
                {
                    cfg.Address = new Uri(options.Url);
                }
            }));

            var registration = builder.CreateConsulAgentRegistration(options);

            if (registration is null)
            {
                return builder;
            }
            
            builder.Services.AddSingleton(registration);
            builder.AddBuildAction(sp =>
            {
                var consulRegistration = sp.GetService<AgentServiceRegistration>();
                var client = sp.GetService<IConsulClient>();

                client.Agent.ServiceRegister(consulRegistration);
            });

            return builder;
        }

        public static IApplicationBuilder UseConsul(this IApplicationBuilder app)
        {
            var options = app.ApplicationServices.GetService<ConsulOptions>();

            if (options.PingEnabled)
            {
                app.Map($"/{options.PingEndpoint}", ab => ab.Run(async ctx => ctx.Response.StatusCode = 200));
            }
            app.DeregisterConsulServiceOnShutdown();
            return app;
        }

        private static void DeregisterConsulServiceOnShutdown(this IApplicationBuilder app)
        {
            var applicationLifetime = app.ApplicationServices.GetService<IApplicationLifetime>();
            var client = app.ApplicationServices.GetService<IConsulClient>(); 
            var registration = app.ApplicationServices.GetService<AgentServiceRegistration>(); 
            applicationLifetime.ApplicationStopped.Register(() => 
                client.Agent.ServiceDeregister(registration.ID));
        }

        private static AgentServiceRegistration CreateConsulAgentRegistration(this IConveyBuilder builder, ConsulOptions options)
        {
                var enabled = options.Enabled;
                var consulEnabled = Environment.GetEnvironmentVariable("CONSUL_ENABLED")?.ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(consulEnabled))
                {
                    enabled = consulEnabled == "true" || consulEnabled == "1";
                }

                if (!enabled)
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(options.Address))
                {
                    throw new ArgumentException("Consul address can not be empty.",
                        nameof(options.PingEndpoint));
                }

                var uniqueId = $"{Guid.NewGuid():N}";
                var pingInterval = options.PingInterval <= 0 ? 5 : options.PingInterval;
                var removeAfterInterval = options.RemoveAfterInterval <= 0 ? 10 : options.RemoveAfterInterval;
                
                var registration = new AgentServiceRegistration
                {
                    Name = options.Service,
                    ID = $"{options.Service}:{uniqueId}",
                    Address = options.Address,
                    Port = options.Port
                };
                
                if (options.PingEnabled)
                {
                    var scheme = options.Address.StartsWith("http", StringComparison.InvariantCultureIgnoreCase)
                        ? string.Empty
                        : "http://";
                    var check = new AgentServiceCheck
                    {
                        Interval = TimeSpan.FromSeconds(pingInterval),
                        DeregisterCriticalServiceAfter = TimeSpan.FromSeconds(removeAfterInterval),
                        HTTP = $"{scheme}{options.Address}{(options.Port > 0 ? $":{options.Port}" : string.Empty)}/{options.PingEndpoint}"
                    };
                    registration.Checks = new[] {check};
                }

                return registration;
        }
    }
}