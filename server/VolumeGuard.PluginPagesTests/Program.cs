using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Plugin.VolumeGuard;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
using (var payload = JsonDocument.Parse(PluginPagesIntegration.RegistrationJson))
{
    Check(payload.RootElement.GetProperty("id").GetString() == "volume-guard", "Stable page id.");
    Check(payload.RootElement.GetProperty("url").GetString() == "/VolumeGuard/page", "Base-path-aware API-relative fragment URL.");
    Check(payload.RootElement.GetProperty("displayText").GetString() == "Volume Guard", "Display text.");
    Check(payload.RootElement.GetProperty("icon").GetString() == "volume_up", "Fixed safe menu icon.");
}
try { PluginPagesIntegration.Register(typeof(Plugin).Assembly); throw new Exception("Missing Plugin Pages interface accepted"); }
catch (MissingMethodException) { checks++; }
// An unrelated similarly named method must not be mistaken for the documented JObject API.
var fixture = AssemblyBuilder.DefineDynamicAssembly(new("VolumeGuard.IncompatiblePluginPagesFixture"), AssemblyBuilderAccess.Run);
var typeBuilder = fixture.DefineDynamicModule("fixture").DefineType("Jellyfin.Plugin.PluginPages.PluginInterface", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
typeBuilder.DefineMethod("RegisterPage", MethodAttributes.Public | MethodAttributes.Static, typeof(void), [typeof(JsonElement)]).GetILGenerator().Emit(OpCodes.Ret);
typeBuilder.CreateType();
try { PluginPagesIntegration.Register(fixture); throw new Exception("Incorrect RegisterPage signature accepted"); }
catch (MissingMethodException) { checks++; }

var binary = Environment.GetEnvironmentVariable("VOLUMEGUARD_PLUGIN_PAGES_ASSEMBLY");
if (string.IsNullOrWhiteSpace(binary) || !File.Exists(binary))
{
    Console.WriteLine($"PASS: {checks} optional Plugin Pages boundary checks. Official binary tests skipped; set VOLUMEGUARD_PLUGIN_PAGES_ASSEMBLY to the official Plugin Pages 3+ DLL built for Jellyfin12.2.");
    return;
}
var folder = Path.Combine(Path.GetTempPath(), "volume-guard-pages-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(binary));
    Check(assembly.GetName().Name == "Jellyfin.Plugin.PluginPages", "Official Plugin Pages assembly identity.");
    var managerType = assembly.GetType("Jellyfin.Plugin.PluginPages.Manager.PluginPagesManager", true)!;
    using var provider = new ServiceCollection().BuildServiceProvider();
    var manager = Activator.CreateInstance(managerType, provider)!;
    var paths = DispatchProxy.Create<IApplicationPaths, PagesPathProxy>();
    ((PagesPathProxy)(object)paths).Folder = folder;
    var xml = DispatchProxy.Create<IXmlSerializer, PagesXmlProxy>();
    var pluginType = assembly.GetType("Jellyfin.Plugin.PluginPages.PluginPagesPlugin", true)!;
    var pluginLogger = Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(pluginType))!;
    var configurationManager = DispatchProxy.Create<IServerConfigurationManager, PagesNullProxy>();
    _ = Activator.CreateInstance(pluginType, paths, xml, pluginLogger, manager, configurationManager);
    object[] Pages() => ((IEnumerable)managerType.GetMethod("GetPages")!.Invoke(manager, null)!).Cast<object>().ToArray();
    PluginPagesIntegration.Register(assembly);
    var pages = Pages();
    Check(pages.Length == 1, "Real upstream manager receives registration.");
    var page = pages.Single();
    Check((string?)page.GetType().GetProperty("Id")!.GetValue(page) == "volume-guard", "Real JObject payload deserializes id.");
    Check((string?)page.GetType().GetProperty("Url")!.GetValue(page) == "/VolumeGuard/page", "Real JObject payload deserializes fragment URL.");
    Check((string?)page.GetType().GetProperty("DisplayText")!.GetValue(page) == "Volume Guard", "Real JObject payload deserializes menu label.");
    PluginPagesIntegration.Register(assembly);
    Check(Pages().Length == 1, "Repeated registration is idempotent with upstream manager.");
    assembly.GetType("Jellyfin.Plugin.PluginPages.PluginInterface", true)!.GetMethod("RemovePage")!.Invoke(null, ["volume-guard"]);
    Check(Pages().Length == 0, "Real upstream removal resets startup test.");
    var lifetime = new PagesLifetime();
    var host = DispatchProxy.Create<IServerApplicationHost, PagesHostProxy>();
    using var service = new PluginPagesIntegration(lifetime, host, NullLogger<PluginPagesIntegration>.Instance);
    await service.StartAsync(default);
    await Task.Delay(100);
    Check(Pages().Length == 0, "Hosted integration waits for ApplicationStarted.");
    lifetime.Start();
    await Task.Delay(150);
    Check(Pages().Length == 0, "Hosted integration also waits for Jellyfin core startup.");
    ((PagesHostProxy)(object)host).CoreReady = true;
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!PluginPagesIntegration.IsRegistered) await Task.Delay(25, deadline.Token);
    Check(Pages().Length == 1, "Hosted integration registers after both startup gates.");
    Check(PluginPagesIntegration.Status.Contains("registered", StringComparison.Ordinal), "Registration diagnostic matches successful API call.");
    await service.StopAsync(default);
    Console.WriteLine($"PASS: {checks} Plugin Pages compatibility checks against the official {assembly.GetName().Name} binary, including real JObject registration, idempotency and Jellyfin startup gates.");
}
finally { Directory.Delete(folder, recursive: true); }

public class PagesPathProxy : DispatchProxy
{
    public string Folder { get; set; } = "";
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name.StartsWith("get_", StringComparison.Ordinal) ? Folder : null;
}
public class PagesXmlProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "DeserializeFromFile")
        {
            using var stream = File.OpenRead((string)args![1]!);
            return new XmlSerializer((Type)args[0]!).Deserialize(stream);
        }
        return null;
    }
}
public class PagesNullProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
}
public class PagesHostProxy : DispatchProxy
{
    public bool CoreReady { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name == "get_CoreStartupHasCompleted" ? CoreReady : null;
}
public sealed class PagesLifetime : IHostApplicationLifetime
{
    private readonly CancellationTokenSource _started = new();
    public CancellationToken ApplicationStarted => _started.Token;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void Start() => _started.Cancel();
    public void StopApplication() { }
}
