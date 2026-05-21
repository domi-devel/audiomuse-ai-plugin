using System.Linq;
using Jellyfin.Plugin.AudioMuseAi.Controller;
using Jellyfin.Plugin.AudioMuseAi.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioMuseAi
{
    /// <summary>
    /// Registers the plugin's services with the DI container.
    /// </summary>
    public class AudioMuseServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            // Register our convention to disable the default Instant Mix HTTP endpoint.
            serviceCollection.AddSingleton<IControllerModelConvention, AudioMuseControllerConvention>();

            // Register controllers.
            serviceCollection.AddTransient<AudioMuseController>();
            serviceCollection.AddTransient<InstantMixController>();

            // Decorate IMusicManager so AudioMuse AI instant mix logic applies to ALL callers,
            // including SessionManager.TranslateItemForInstantMix() used during DLNA casting.
            // That code path calls IMusicManager directly and never reaches the HTTP controller.
            var originalDescriptor = serviceCollection.FirstOrDefault(d => d.ServiceType == typeof(IMusicManager));
            if (originalDescriptor != null)
            {
                serviceCollection.Remove(originalDescriptor);
                serviceCollection.Add(new ServiceDescriptor(
                    typeof(IMusicManager),
                    sp =>
                    {
                        IMusicManager inner;
                        if (originalDescriptor.ImplementationInstance != null)
                        {
                            inner = (IMusicManager)originalDescriptor.ImplementationInstance;
                        }
                        else if (originalDescriptor.ImplementationFactory != null)
                        {
                            inner = (IMusicManager)originalDescriptor.ImplementationFactory(sp);
                        }
                        else
                        {
                            inner = (IMusicManager)Microsoft.Extensions.DependencyInjection.ActivatorUtilities
                                .CreateInstance(sp, originalDescriptor.ImplementationType!);
                        }

                        return new AudioMuseMusicManagerDecorator(
                            inner,
                            sp.GetRequiredService<ILibraryManager>(),
                            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
                            sp.GetRequiredService<ILogger<AudioMuseMusicManagerDecorator>>());
                    },
                    originalDescriptor.Lifetime));
            }
        }
    }
}
