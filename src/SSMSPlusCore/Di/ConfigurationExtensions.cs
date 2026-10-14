using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SSMSPlusCore.Utils.Validation;

namespace SSMSPlusCore.Di
{
    public static class ConfigurationExtensions
    {
        public static void ConfigureSection<TSection>(this IServiceCollection services, IConfiguration config) where TSection : class, IValidatable<TSection>, new()
        {
            services.Configure<TSection>(config);
            services.AddSingleton(resolver => resolver.GetRequiredService<IOptions<TSection>>().Value.Validate());
        }
    }
}
