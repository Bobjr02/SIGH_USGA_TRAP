using Microsoft.Extensions.DependencyInjection;

namespace SIGH.Demo;

public static class DemoDataServiceCollectionExtensions
{
    /// <summary>
    /// Registra o seeder de demonstração SÓ quando <paramref name="demoData"/> (Seed:DemoData) está ligado. Sem ele,
    /// nada deste projeto é registrado nem executado (e o OpenXml nem é carregado).
    /// </summary>
    public static IServiceCollection AddDemoData(this IServiceCollection services, bool demoData)
    {
        if (demoData)
            services.AddScoped<DemoDataSeeder>();
        return services;
    }
}
