using JestStreamEngineManager.Configuration;
using JestStreamEngineManager.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.Versioning;

namespace JestStreamEngineManager;

internal sealed class Program
{
    private Program() { }

    public static async Task<int> Main(string[] args)
    {
        IHost? host = null;

        try
        {
            host = CreateHostBuilder(args).Build();
            await ValidateServicesAsync(host);
            return await RunHostAsync(host);
        }
        catch (Exception ex)
        {
            if (Environment.UserInteractive)
            {
                Console.WriteLine($"Fatal error during startup: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
            }
            else if (OperatingSystem.IsWindows())
            {
                try
                {
                    System.Diagnostics.EventLog.WriteEntry(
                        "JestStream Engine Manager",
                        $"Fatal error during startup: {ex.Message}\n\nStack trace:\n{ex.StackTrace}",
                        System.Diagnostics.EventLogEntryType.Error);
                }
                catch { /* ignore */ }
            }

            return 1;
        }
        finally
        {
            host?.Dispose();
        }
    }

    private static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseWindowsService(options =>
            {
                options.ServiceName = "JestStream Engine Manager";
            })
            .ConfigureLogging((_, logging) =>
            {
                if (Environment.UserInteractive)
                {
                    logging.AddConsole();
                }
                else if (OperatingSystem.IsWindows())
                {
                    ConfigureWindowsEventLog(logging);
                }
            })
            .ConfigureServices((context, services) =>
            {
                var configSection = context.Configuration.GetSection(JestStreamEngineManagerSettings.SectionName);

                if (!configSection.GetChildren().Any())
                    throw new InvalidOperationException(
                        $"Configuration section '{JestStreamEngineManagerSettings.SectionName}' is missing from appsettings.json");

                services.Configure<JestStreamEngineManagerSettings>(configSection);
                services.AddOptions<JestStreamEngineManagerSettings>()
                    .Bind(configSection)
                    .ValidateDataAnnotations();

                // MediaMTX HTTP client (no auth required)
                services.AddHttpClient<IMediaMtxRepository, MediaMtxRepository>();

                // JetStream call change repository (manages its own NATS connection)
                services.AddScoped<ICallChangeRepository, CallChangeRepository>();

                services.AddHostedService<JestStreamEngineManagerService>();

                if (Environment.UserInteractive)
                    Console.WriteLine("Services configured successfully");
            });

    private static async Task ValidateServicesAsync(IHost host)
    {
        try
        {
            using var scope = host.Services.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IOptions<JestStreamEngineManagerSettings>>();
            _ = settings.Value; // triggers DataAnnotations validation

            scope.ServiceProvider.GetRequiredService<IMediaMtxRepository>();
            scope.ServiceProvider.GetRequiredService<ICallChangeRepository>();

            await Task.Delay(1);

            if (Environment.UserInteractive)
                Console.WriteLine("Service validation completed successfully");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Service validation failed: {ex.Message}", ex);
        }
    }

    private static async Task<int> RunHostAsync(IHost host)
    {
        try
        {
            await host.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            var logger = host.Services.GetRequiredService<ILogger<Program>>();
            logger.LogCritical(ex, "Host execution failed");
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ConfigureWindowsEventLog(ILoggingBuilder logging)
    {
        logging.AddEventLog(s =>
        {
            s.SourceName = "JestStream Engine Manager";
            s.LogName = "Application";
        });
    }
}
