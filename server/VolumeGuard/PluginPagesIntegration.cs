using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VolumeGuard;

/// <summary>Optional Plugin Pages v3 reflection boundary; no third-party runtime dependency.</summary>
public sealed class PluginPagesIntegration(IHostApplicationLifetime lifetime, IServerApplicationHost host, ILogger<PluginPagesIntegration> logger) : BackgroundService
{
    public const string PageId = "volume-guard";
    public const string PageUrl = "/VolumeGuard/page";
    private static string _status = "Waiting for Jellyfin startup to finish.";
    private static bool _registered;
    public static string Status => Volatile.Read(ref _status);
    public static bool IsRegistered => Volatile.Read(ref _registered);
    public static string RegistrationJson => JsonSerializer.Serialize(new { id = PageId, url = PageUrl, displayText = "Volume Guard", icon = "volume_up" });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Plugin construction and Plugin Pages' manager must finish first. Hosted
            // services can start before Jellyfin completes its separate core startup.
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var startupRegistration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
            await started.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
            while (!host.CoreStartupHasCompleted) await Task.Delay(100, stoppingToken).ConfigureAwait(false);
            var assembly = AssemblyLoadContext.All.SelectMany(context => context.Assemblies)
                .FirstOrDefault(value => value.GetName().Name == "Jellyfin.Plugin.PluginPages");
            if (assembly is null)
            {
                _status = "Plugin Pages is not installed. The dashboard settings page remains available.";
                return;
            }
            for (var attempt = 0; attempt < 5; attempt++)
            {
                stoppingToken.ThrowIfCancellationRequested();
                try
                {
                    Register(assembly);
                    _registered = true;
                    _status = "Volume Guard is registered in Plugin Pages. Editing settings requires a Jellyfin administrator.";
                    return;
                }
                catch (TargetInvocationException) when (attempt < 4)
                {
                    // A compatible plugin may still be finishing its manager setup.
                    // Registration is idempotent upstream by page id.
                    await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            _status = "Plugin Pages registration failed. Use the dashboard settings page and check Plugin Pages 3+ compatibility.";
            logger.LogWarning(error, "Volume Guard could not register its optional Plugin Pages entry");
        }
    }

    /// <summary>Matches upstream v3 RegisterPage(JObject) exactly, without a compiled reference.</summary>
    public static void Register(Assembly assembly)
    {
        var type = assembly.GetType("Jellyfin.Plugin.PluginPages.PluginInterface", throwOnError: false)
            ?? throw new MissingMethodException("Plugin Pages 3+ PluginInterface is unavailable.");
        var register = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(method => method.Name == "RegisterPage" && method.ReturnType == typeof(void)
                && method.GetParameters() is [{ ParameterType.FullName: "Newtonsoft.Json.Linq.JObject" }])
            ?? throw new MissingMethodException("Plugin Pages 3+ RegisterPage(JObject) is unavailable.");
        var payloadType = register.GetParameters()[0].ParameterType;
        var parse = payloadType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
        if (parse is null || !payloadType.IsAssignableFrom(parse.ReturnType))
            throw new MissingMethodException("Plugin Pages JObject.Parse(string) is unavailable.");
        var payload = parse.Invoke(null, [RegistrationJson]) ?? throw new InvalidOperationException("Plugin Pages payload could not be created.");
        register.Invoke(null, [payload]);
    }
}
