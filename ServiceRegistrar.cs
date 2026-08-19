using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller;
using Unpublish.Middleware;

namespace Unpublish
{
    public class ServiceRegistrar : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddTransient<IStartupFilter, SpoilerHidingStartupFilter>();
        }
    }
}
