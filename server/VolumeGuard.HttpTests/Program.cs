using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Plugin.VolumeGuard;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

var folder = Path.Combine(AppContext.BaseDirectory, "http-test-config", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
var paths = DispatchProxy.Create<IApplicationPaths, PathProxy>();
((PathProxy)(object)paths).Folder = folder;
var xml = DispatchProxy.Create<IXmlSerializer, XmlProxy>();
_ = new Plugin(paths, xml);
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("Test", _ => { });
builder.Services.AddAuthorization(options => options.AddPolicy("RequiresElevation", policy => policy.RequireAuthenticatedUser().RequireRole("Admin")));
builder.Services.AddControllers(options => options.Filters.Add<VolumeGuardPlaybackFilter>()).AddApplicationPart(typeof(VolumeGuardController).Assembly);
var originalManager = DispatchProxy.Create<ITranscodeManager, TranscodeProxy>();
builder.Services.AddSingleton(originalManager);
var sessionManager = DispatchProxy.Create<ISessionManager, SessionProxy>();
var statusSession = new SessionInfo(null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionInfo>.Instance)
    { Id = "status-session", DeviceId = "status-device", DeviceName = "Status test device", Client = "Moonfin", LastActivityDate = DateTime.UtcNow };
((SessionProxy)(object)sessionManager).Sessions = [statusSession];
builder.Services.AddSingleton(sessionManager);
ServiceRegistrator.DecorateTranscodeManager(builder.Services);
await using var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
await app.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
var checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
async Task<HttpResponseMessage> Request(HttpMethod method, string route, string? role = null, object? body = null, string? deviceId = null)
{
    using var request = new HttpRequestMessage(method, route);
    if (role is not null) request.Headers.Add("X-Test-Role", role);
    if (deviceId is not null) request.Headers.Add("X-Test-Device", deviceId);
    if (body is not null) request.Content = JsonContent.Create(body);
    return await client.SendAsync(request);
}
Check((await Request(HttpMethod.Get, "/VolumeGuard/policy")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous policy denied");
Check((await Request(HttpMethod.Get, "/VolumeGuard/configuration")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous configuration denied");
Check((await Request(HttpMethod.Post, "/VolumeGuard/configuration", body: new Configuration())).StatusCode == HttpStatusCode.Unauthorized, "Anonymous write denied");
Check((await Request(HttpMethod.Get, "/VolumeGuard/configuration", "User")).StatusCode == HttpStatusCode.Forbidden, "Non-admin read denied");
Check((await Request(HttpMethod.Post, "/VolumeGuard/configuration", "User", new Configuration())).StatusCode == HttpStatusCode.Forbidden, "Non-admin write denied");
Check((await Request(HttpMethod.Get, "/VolumeGuard/page")).StatusCode == HttpStatusCode.Unauthorized, "Plugin Pages shell requires authentication");
var pluginPage = await Request(HttpMethod.Get, "/VolumeGuard/page", "User");
Check(pluginPage.StatusCode == HttpStatusCode.OK, "Authenticated Plugin Pages shell is available");
Check(pluginPage.Content.Headers.ContentType?.MediaType == "text/html", "Plugin Pages shell is HTML");
Check(pluginPage.Headers.CacheControl?.NoStore == true, "Plugin Pages shell is not cached across sessions");
var fragment = await pluginPage.Content.ReadAsStringAsync();
Check(fragment.Contains("data-vg-plugin-pages=\"true\"", StringComparison.Ordinal), "Plugin Pages shell marks immediate initialization");
Check(!fragment.Contains("<html", StringComparison.OrdinalIgnoreCase) && !fragment.Contains("<body", StringComparison.OrdinalIgnoreCase), "Plugin Pages shell excludes document wrapper");
Check(!fragment.Contains("data-role=\"page\"", StringComparison.Ordinal) && !fragment.Contains("pluginConfigurationPage", StringComparison.Ordinal), "Plugin Pages shell does not nest Jellyfin router pages");
Check(fragment.Contains("vg-dialogue", StringComparison.Ordinal) && fragment.Contains("vg-devices", StringComparison.Ordinal), "Plugin Pages shell uses complete settings component");
var policyResponse = await Request(HttpMethod.Get, "/VolumeGuard/policy?userId=someone-else", "User");
Check(policyResponse.StatusCode == HttpStatusCode.OK, "Authenticated policy works without userId claim");
using var policyJson = JsonDocument.Parse(await policyResponse.Content.ReadAsStringAsync());
Check(policyJson.RootElement.GetProperty("schemaVersion").GetInt32() == 1, "HTTP camelCase schema");
Check(policyJson.RootElement.GetProperty("configuration").GetProperty("balanced").GetProperty("targetRmsDb").GetDouble() == -20, "HTTP nested profile");
var invalid = new Configuration { TimeZoneId = "Bad/Zone" };
var badResponse = await Request(HttpMethod.Post, "/VolumeGuard/configuration", "Admin", invalid);
Check(badResponse.StatusCode == HttpStatusCode.BadRequest, "Invalid admin configuration rejected");
Check((await badResponse.Content.ReadAsStringAsync()).Contains("timeZoneId"), "Validation ProblemDetails reason");
Check(Plugin.Instance.Configuration.TimeZoneId == "UTC", "Rejected update leaves configuration intact");
var update = new Configuration { DaytimeMode = "Off", DefaultMode = "Night", TimeZoneId = "America/New_York" };
update.Night.MaxGainDb = 7;
Check((await Request(HttpMethod.Post, "/VolumeGuard/configuration", "Admin", update)).StatusCode == HttpStatusCode.NoContent, "Valid admin update accepted");
Check(Directory.GetFiles(folder, "*.xml").Length == 1, "Jellyfin persistence writes XML configuration");
_ = new Plugin(paths, xml);
Check(Plugin.Instance.Configuration.DefaultMode == "Night" && Plugin.Instance.Configuration.Night.MaxGainDb == 7, "New plugin instance reloads persisted values");
var adminRead = await Request(HttpMethod.Get, "/VolumeGuard/configuration", "Admin");
Check(adminRead.StatusCode == HttpStatusCode.OK, "Admin configuration read");
Check((await adminRead.Content.ReadAsStringAsync()).Contains("\"daytimeMode\":\"Off\""), "Reloaded configuration through HTTP");
Check((await Request(HttpMethod.Get, "/VolumeGuard/status")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous processing status denied");
Check((await Request(HttpMethod.Get, "/VolumeGuard/status", "User")).StatusCode == HttpStatusCode.Forbidden, "Non-admin processing status denied");
Check((await Request(HttpMethod.Get, "/VolumeGuard/status", "Admin")).StatusCode == HttpStatusCode.OK, "Admin processing status available without live sessions");
var invalidDevice = new Configuration { DeviceOverrides = [new() { DeviceId = "same" }, new() { DeviceId = "same" }] };
Check((await Request(HttpMethod.Post, "/VolumeGuard/configuration", "Admin", invalidDevice)).StatusCode == HttpStatusCode.BadRequest, "Duplicate device IDs rejected");
Check((await Request(HttpMethod.Post, "/VolumeGuard/configuration", "Admin", new Configuration { DialogueBoostDb = 13 })).StatusCode == HttpStatusCode.BadRequest, "Excess dialogue boost rejected");
Check((await Request(HttpMethod.Post, "/VolumeGuard/configuration", "Admin", new Configuration { AnalysisTargetLufs = -9 })).StatusCode == HttpStatusCode.BadRequest, "Invalid measured loudness target rejected");
// Jellyfin's generic plugin endpoint can bypass the custom validation. Fail policy safely.
Plugin.Instance.UpdateConfiguration(new Configuration { TimeZoneId = "Bad/Zone" });
Check((await Request(HttpMethod.Get, "/VolumeGuard/policy", "User")).StatusCode == HttpStatusCode.ServiceUnavailable, "Invalid external configuration produces 503");
Plugin.Instance.UpdateConfiguration(new Configuration());
var originalPlayback = await Request(HttpMethod.Post, "/test/playback?enableDirectPlay=true&allowAudioStreamCopy=true&allowVideoStreamCopy=false&enableTranscoding=false", "User", new TestPlaybackBody());
Check((await originalPlayback.Content.ReadAsStringAsync()).Contains("query=True;audio=True;video=False"), "Disabled plugin preserves all playback negotiation");
Plugin.Instance.UpdateConfiguration(new Configuration { ServerProcessingEnabled = true, DefaultMode = "Balanced" });
var processedPlayback = await Request(HttpMethod.Post, "/test/playback?enableDirectPlay=true&allowAudioStreamCopy=true&allowVideoStreamCopy=false&enableTranscoding=false", "User", new TestPlaybackBody());
var processedJson = JsonDocument.Parse(await processedPlayback.Content.ReadAsStringAsync());
var processedSource = processedJson.RootElement.GetProperty("mediaSources")[0];
Check(processedSource.GetProperty("name").GetString() == "query=False;audio=False;video=False", "Query/body precedence overridden without changing video copy");
Check(processedSource.GetProperty("supportsTranscoding").GetBoolean(), "Moonfin direct Live TV preference does not prevent requesting permitted conversion");
Check(!processedSource.GetProperty("supportsDirectPlay").GetBoolean() && !processedSource.GetProperty("supportsDirectStream").GetBoolean(), "Stock client receives no direct route");
Check(processedSource.GetProperty("path").ValueKind == JsonValueKind.Null, "Moonfin raw live URL bypass suppressed");
Check(processedSource.GetProperty("transcodingUrl").GetString()!.Contains("allowAudioStreamCopy=false"), "Encoding URL prohibits audio copy");
Check(MediaInfoController.Source.Path == "https://upstream.example/live.ts" && MediaInfoController.Source.SupportsDirectPlay, "Shared server source remains intact");
var bodyOnly = await Request(HttpMethod.Post, "/test/playback", "User", new TestPlaybackBody());
Check((await bodyOnly.Content.ReadAsStringAsync()).Contains("query=False;audio=False;video="), "Body only negotiation cannot reenable direct play/audio copy");
Check((await Request(HttpMethod.Post, "/test/live", "User")).StatusCode == HttpStatusCode.OK, "Live stream response sanitized without shared mutation");
Check((await Request(HttpMethod.Post, "/test/unsupported", "User")).StatusCode == HttpStatusCode.Conflict, "No permitted encoding route fails instead of Moonfin terminal direct fallback");
Check((await Request(HttpMethod.Post, "/test/playback")).StatusCode == HttpStatusCode.Unauthorized, "Playback filter retains authentication requirement");
var perDevice = new Configuration { ServerProcessingEnabled = true, DefaultMode = "Balanced", DialogueBoostDb = 3,
    DeviceOverrides = [new() { DeviceId = "bedroom", Mode = "Night", DialogueBoostDb = 6 }, new() { DeviceId = "living", Mode = "Off" }] };
Plugin.Instance.UpdateConfiguration(perDevice);
Check(PlaybackSettings.Resolve("bedroom").DefaultMode == "Night" && PlaybackSettings.Resolve("bedroom").DialogueBoostDb == 6, "Device override applied");
Check(PlaybackSettings.Resolve("other").DefaultMode == "Balanced" && PlaybackSettings.Resolve("other").DialogueBoostDb == 3, "Unknown device inherits global settings");
var bedroomCopy = PlaybackSettings.Resolve("bedroom"); bedroomCopy.Night.MaxGainDb = 0;
Check(perDevice.DefaultMode == "Balanced" && perDevice.Night.MaxGainDb == 9, "Resolving device never mutates global profiles");
var untouched = await Request(HttpMethod.Post, "/test/playback?enableDirectPlay=true&allowAudioStreamCopy=true", "User", new TestPlaybackBody(), "living");
Check((await untouched.Content.ReadAsStringAsync()).Contains("query=True;audio=True"), "Device Off preserves native direct playback");
var bedroom = await Request(HttpMethod.Post, "/test/playback?enableDirectPlay=true&allowAudioStreamCopy=true", "User", new TestPlaybackBody(), "bedroom");
Check((await bedroom.Content.ReadAsStringAsync()).Contains("query=False;audio=False"), "Authenticated device Night forces processing");
perDevice.Enabled = false;
Check(PlaybackSettings.Mode(PlaybackSettings.Resolve("bedroom"), DateTimeOffset.UtcNow) == "Off", "Global master overrides device enable");
Plugin.Instance.UpdateConfiguration(new Configuration { ServerProcessingEnabled = true, DefaultMode = "Balanced" });
var manager = app.Services.GetRequiredService<ITranscodeManager>();
Check(manager is VolumeGuardTranscodeManager, "Jellyfin manager descriptor decorated through real DI");
manager.PingTranscodingJob("test", true);
Check(((TranscodeProxy)(object)originalManager).LastMethod == "PingTranscodingJob", "Lifecycle call delegated");
using var cancel = new CancellationTokenSource();
var state = new StreamState(null!, TranscodingJobType.Hls, manager) { Request = new VideoRequestDto { PlaySessionId = "test" }, AudioStream = new MediaBrowser.Model.Entities.MediaStream(), OutputAudioCodec = "aac" };
await manager.StartFfMpeg(state, "out", "-i in -c:v copy -c:a aac -af \"volume=1\" out", Guid.Empty, TranscodingJobType.Hls, cancel);
Check(((TranscodeProxy)(object)originalManager).LastArguments![2]!.ToString()!.Contains("volume=1,dynaudnorm="), "DSP injected before real manager process launch");
Plugin.Instance.UpdateConfiguration(new Configuration { ServerProcessingEnabled = true, DefaultMode = "Balanced", DialogueBoostDb = 6 });
state.AudioStream.ChannelLayout = "5.1";
await manager.StartFfMpeg(state, "out", "-i in -c:v copy -c:a aac -af \"pan=stereo|FL=FL+FC|FR=FR+FC\" out", Guid.Empty, TranscodingJobType.Hls, cancel);
var dialogueArguments = ((TranscodeProxy)(object)originalManager).LastArguments![2]!.ToString()!;
Check(dialogueArguments.IndexOf("pan=5.1", StringComparison.Ordinal) < dialogueArguments.IndexOf("pan=stereo", StringComparison.Ordinal), "Dialogue center emphasis precedes existing downmix");
Check(dialogueArguments.IndexOf("pan=stereo", StringComparison.Ordinal) < dialogueArguments.IndexOf("dynaudnorm=", StringComparison.Ordinal), "Leveling and limiter remain after downmix");
var statusItemId = Guid.NewGuid();
statusSession.NowPlayingItem = new BaseItemDto { Id = statusItemId };
statusSession.PlayState.PlayMethod = MediaBrowser.Model.Session.PlayMethod.Transcode;
using var statusJob = new TranscodingJob(Microsoft.Extensions.Logging.Abstractions.NullLogger<TranscodingJob>.Instance) { PlaySessionId = "status-play" };
((TranscodeProxy)(object)originalManager).Job = statusJob;
ProcessingRegistry.Record(new("status-device", "status-play", "Balanced", "Speech EQ", "Disabled", "test", DateTimeOffset.UtcNow, "launched", "test launch", statusItemId));
async Task<bool> ConfirmedStatus()
{
    var response = await Request(HttpMethod.Get, "/VolumeGuard/status", "Admin");
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return json.RootElement.GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("deviceId").GetString() == "status-device").GetProperty("processing").GetBoolean();
}
ProcessingRegistry.ObservePlayback("status-session", "old-play");
Check(!await ConfirmedStatus(), "Old same-title encoding job is not current playback confirmation");
ProcessingRegistry.ObservePlayback("status-session", "status-play");
Check(await ConfirmedStatus(), "Matching playback token and encoder job confirm processing");
ProcessingRegistry.StopPlayback("status-session", "old-play");
Check(await ConfirmedStatus(), "Late stop for old playback does not clear current confirmation");
ProcessingRegistry.StopPlayback("status-session", "status-play");
Check(!await ConfirmedStatus(), "Stopped playback cannot remain confirmed");
((TranscodeProxy)(object)originalManager).Job = null;
state.OutputAudioCodec = "copy";
try { await manager.StartFfMpeg(state, "out", "-i in -c:a copy out", Guid.Empty, TranscodingJobType.Hls, cancel); throw new Exception("copy accepted"); }
catch (InvalidOperationException) { checks++; }
Plugin.Instance.UpdateConfiguration(new Configuration());
await manager.StartFfMpeg(state, "out", "original args unchanged", Guid.Empty, TranscodingJobType.Hls, cancel);
Check((string)((TranscodeProxy)(object)originalManager).LastArguments![2]! == "original args unchanged", "Disabled DSP passes exact original arguments");
await app.StopAsync();
Console.WriteLine($"PASS: {checks} HTTP authentication, authorization, validation, persistence and JSON checks.");

public class PathProxy : DispatchProxy
{
    public string Folder { get; set; } = "";
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name.StartsWith("get_", StringComparison.Ordinal) ? Folder : null;
}
public class XmlProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "SerializeToFile":
                using (var stream = File.Create((string)args![1]!)) new XmlSerializer(args[0]!.GetType()).Serialize(stream, args[0]);
                return null;
            case "DeserializeFromFile":
                using (var stream = File.OpenRead((string)args![1]!)) return new XmlSerializer((Type)args[0]!).Deserialize(stream);
            case "SerializeToStream": new XmlSerializer(args![0]!.GetType()).Serialize((Stream)args[1]!, args[0]); return null;
            case "DeserializeFromStream": return new XmlSerializer((Type)args![0]!).Deserialize((Stream)args[1]!);
            default: throw new NotSupportedException(method.Name);
        }
    }
}
public class TestAuthentication : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : base(options, logger, encoder) { }
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Role", out var role)) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "test"), new Claim(ClaimTypes.Role, role.ToString())], "Test");
        if (Request.Headers.TryGetValue("X-Test-Device", out var device)) identity.AddClaim(new Claim("Jellyfin-DeviceId", device.ToString()));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
    }
}

public class TranscodeProxy : DispatchProxy
{
    public TranscodingJob? Job { get; set; }
    public string? LastMethod { get; private set; }
    public object?[]? LastArguments { get; private set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        LastMethod = method!.Name; LastArguments = args;
        return method.Name == "StartFfMpeg" ? Task.FromResult<TranscodingJob>(null!) : method.Name == "GetTranscodingJob" ? Job : null;
    }
}

public class SessionProxy : DispatchProxy
{
    public SessionInfo[] Sessions { get; set; } = [];
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == "get_Sessions" ? Sessions : null;
}

public sealed class TestPlaybackBody { public bool EnableDirectPlay { get; set; } = true; public bool AllowAudioStreamCopy { get; set; } = true; }
[ApiController, Authorize]
public sealed class MediaInfoController : ControllerBase
{
    public static MediaSourceInfo Source { get; } = new() { Id = "test", Path = "https://upstream.example/live.ts", LiveStreamId = "live", SupportsDirectPlay = true, SupportsDirectStream = true, SupportsTranscoding = true, TranscodingUrl = "/Videos/test/master.m3u8?AudioCodec=aac&allowAudioStreamCopy=true&Token=test" };
    [HttpPost("/test/playback")]
    public ActionResult<PlaybackInfoResponse> GetPostedPlaybackInfo([FromQuery] bool? enableDirectPlay, [FromQuery] bool? enableDirectStream, [FromQuery] bool? allowAudioStreamCopy, [FromQuery] bool? allowVideoStreamCopy, [FromQuery] bool? enableTranscoding, [FromBody] TestPlaybackBody? body)
    {
        var source = System.Text.Json.JsonSerializer.Deserialize<MediaSourceInfo>(System.Text.Json.JsonSerializer.Serialize(Source))!;
        source.Name = $"query={enableDirectPlay ?? body?.EnableDirectPlay};audio={allowAudioStreamCopy ?? body?.AllowAudioStreamCopy};video={allowVideoStreamCopy}";
        source.SupportsTranscoding = enableTranscoding ?? true;
        return new PlaybackInfoResponse { PlaySessionId = "test", MediaSources = [source] };
    }
    [HttpPost("/test/live")]
    public ActionResult<LiveStreamResponse> OpenLiveStream([FromQuery] bool? enableDirectPlay, [FromQuery] bool? enableDirectStream) => new LiveStreamResponse(Source);
    [HttpPost("/test/unsupported"), ActionName("GetPostedPlaybackInfo")]
    public ActionResult<PlaybackInfoResponse> Unsupported([FromQuery] bool? enableDirectPlay, [FromQuery] bool? enableDirectStream, [FromQuery] bool? allowAudioStreamCopy) => new PlaybackInfoResponse { MediaSources = [new MediaSourceInfo { SupportsTranscoding = false }] };
}
