using System;
using System.Linq;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace StrmTool
{
    /// <summary>
    /// 注册 IMediaSourceManager 装饰器，修复 Jellyfin 仅对 Video 处理 .strm ShortcutPath 而遗漏 Audio 的问题。
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public static bool IsRegistered { get; private set; }

        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            var existingDescriptor = serviceCollection.LastOrDefault(d => d.ServiceType == typeof(IMediaSourceManager));
            if (existingDescriptor == null)
            {
                IsRegistered = false;
                Console.Error.WriteLine(
                    "[StrmTool] Warning: IMediaSourceManager service descriptor not found during plugin service registration; STRM audio playback support is inactive.");
                return;
            }

            serviceCollection.Remove(existingDescriptor);
            serviceCollection.Add(new ServiceDescriptor(
                typeof(IMediaSourceManager),
                sp =>
                {
                    var inner = ResolveDescriptor(sp, existingDescriptor);
                    var logger = sp.GetRequiredService<ILogger<StrmMediaSourceManagerDecorator>>();
                    return new StrmMediaSourceManagerDecorator(inner, logger);
                },
                existingDescriptor.Lifetime));
            IsRegistered = true;
        }

        private static IMediaSourceManager ResolveDescriptor(IServiceProvider serviceProvider, ServiceDescriptor descriptor)
        {
            if (descriptor.ImplementationInstance is IMediaSourceManager instance)
            {
                return instance;
            }

            if (descriptor.ImplementationFactory != null)
            {
                return (IMediaSourceManager)descriptor.ImplementationFactory(serviceProvider);
            }

            if (descriptor.ImplementationType != null)
            {
                return (IMediaSourceManager)ActivatorUtilities.CreateInstance(serviceProvider, descriptor.ImplementationType);
            }

            throw new InvalidOperationException("Unable to resolve inner IMediaSourceManager service descriptor.");
        }
    }
}
