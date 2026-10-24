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
ServiceRegistrator.DecorateTranscodeManager(builder.Services);
await using var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
await app.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
var checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
async Task<HttpResponseMessage> Request(HttpMethod method, string route, string? role = null, object? body = null)
{
    using var request = new HttpRequestMessage(method, route);
    if (role is not null) request.Headers.Add("X-Test-Role", role);
    if (body is not null) request.Content = JsonContent.Create(body);
    return await client.SendAsync(request);
}
Check((await Request(HttpMethod.Get, "/VolumeGuard/policy")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous policy denied");
Check((await Request(HttpMethod.Get, "/VolumeGuard/configuration")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous configuration denied");
Check((await Request(HttpMethod.Post, "/VolumeGuard/configuration", body: new Configuration())).StatusCode == HttpStatusCode.Unauthorized, "Anonymous write denied");
Check((await Request(HttpMethod.Get, "/VolumeGuard/configuration", "User")).StatusCode == HttpStatusCode.Forbidden, "Non-admin read denied");
Check((await Request(HttpMethod.Post, "/VolumeGuard/configuration", "User", new Configuration())).StatusCode == HttpStatusCode.Forbidden, "Non-admin write denied");
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
// Jellyfin's generic plugin endpoint can bypass the custom validation. Fail policy safely.
Plugin.Instance.UpdateConfiguration(new Configuration { TimeZoneId = "Bad/Zone" });
Check((await Request(HttpMethod.Get, "/VolumeGuard/policy", "User")).StatusCode == HttpStatusCode.ServiceUnavailable, "Invalid external configuration produces 503");
Plugin.Instance.UpdateConfiguration(new Configuration());
var originalPlayback = await Request(HttpMethod.Post, "/test/playback?enableDirectPlay=true&allowAudioStreamCopy=true&allowVideoStreamCopy=false", "User", new TestPlaybackBody());
Check((await originalPlayback.Content.ReadAsStringAsync()).Contains("query=True;audio=True;video=False"), "Disabled plugin preserves all playback negotiation");
Plugin.Instance.UpdateConfiguration(new Configuration { ServerProcessingEnabled = true, DefaultMode = "Balanced" });
var processedPlayback = await Request(HttpMethod.Post, "/test/playback?enableDirectPlay=true&allowAudioStreamCopy=true&allowVideoStreamCopy=false", "User", new TestPlaybackBody());
var processedJson = JsonDocument.Parse(await processedPlayback.Content.ReadAsStringAsync());
var processedSource = processedJson.RootElement.GetProperty("mediaSources")[0];
Check(processedSource.GetProperty("name").GetString() == "query=False;audio=False;video=False", "Query/body precedence overridden without changing video copy");
Check(!processedSource.GetProperty("supportsDirectPlay").GetBoolean() && !processedSource.GetProperty("supportsDirectStream").GetBoolean(), "Stock client receives no direct route");
Check(processedSource.GetProperty("path").ValueKind == JsonValueKind.Null, "Moonfin raw live URL bypass suppressed");
Check(processedSource.GetProperty("transcodingUrl").GetString()!.Contains("allowAudioStreamCopy=false"), "Encoding URL prohibits audio copy");
Check(MediaInfoController.Source.Path == "https://upstream.example/live.ts" && MediaInfoController.Source.SupportsDirectPlay, "Shared server source remains intact");
var bodyOnly = await Request(HttpMethod.Post, "/test/playback", "User", new TestPlaybackBody());
Check((await bodyOnly.Content.ReadAsStringAsync()).Contains("query=False;audio=False;video="), "Body only negotiation cannot reenable direct play/audio copy");
Check((await Request(HttpMethod.Post, "/test/live", "User")).StatusCode == HttpStatusCode.OK, "Live stream response sanitized without shared mutation");
Check((await Request(HttpMethod.Post, "/test/unsupported", "User")).StatusCode == HttpStatusCode.Conflict, "No permitted encoding route fails instead of Moonfin terminal direct fallback");
Check((await Request(HttpMethod.Post, "/test/playback")).StatusCode == HttpStatusCode.Unauthorized, "Playback filter retains authentication requirement");
var manager = app.Services.GetRequiredService<ITranscodeManager>();
Check(manager is VolumeGuardTranscodeManager, "Jellyfin manager descriptor decorated through real DI");
manager.PingTranscodingJob("test", true);
Check(((TranscodeProxy)(object)originalManager).LastMethod == "PingTranscodingJob", "Lifecycle call delegated");
using var cancel = new CancellationTokenSource();
var state = new StreamState(null!, TranscodingJobType.Hls, manager) { Request = new VideoRequestDto { PlaySessionId = "test" }, AudioStream = new MediaBrowser.Model.Entities.MediaStream(), OutputAudioCodec = "aac" };
await manager.StartFfMpeg(state, "out", "-i in -c:v copy -c:a aac -af \"volume=1\" out", Guid.Empty, TranscodingJobType.Hls, cancel);
Check(((TranscodeProxy)(object)originalManager).LastArguments![2]!.ToString()!.Contains("volume=1,dynaudnorm="), "DSP injected before real manager process launch");
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
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
    }
}

public class TranscodeProxy : DispatchProxy
{
    public string? LastMethod { get; private set; }
    public object?[]? LastArguments { get; private set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        LastMethod = method!.Name; LastArguments = args;
        return method.Name == "StartFfMpeg" ? Task.FromResult<TranscodingJob>(null!) : null;
    }
}

public sealed class TestPlaybackBody { public bool EnableDirectPlay { get; set; } = true; public bool AllowAudioStreamCopy { get; set; } = true; }
[ApiController, Authorize]
public sealed class MediaInfoController : ControllerBase
{
    public static MediaSourceInfo Source { get; } = new() { Id = "test", Path = "https://upstream.example/live.ts", LiveStreamId = "live", SupportsDirectPlay = true, SupportsDirectStream = true, SupportsTranscoding = true, TranscodingUrl = "/Videos/test/master.m3u8?AudioCodec=aac&allowAudioStreamCopy=true&Token=test" };
    [HttpPost("/test/playback")]
    public ActionResult<PlaybackInfoResponse> GetPostedPlaybackInfo([FromQuery] bool? enableDirectPlay, [FromQuery] bool? enableDirectStream, [FromQuery] bool? allowAudioStreamCopy, [FromQuery] bool? allowVideoStreamCopy, [FromBody] TestPlaybackBody? body)
    {
        var source = System.Text.Json.JsonSerializer.Deserialize<MediaSourceInfo>(System.Text.Json.JsonSerializer.Serialize(Source))!;
        source.Name = $"query={enableDirectPlay ?? body?.EnableDirectPlay};audio={allowAudioStreamCopy ?? body?.AllowAudioStreamCopy};video={allowVideoStreamCopy}";
        return new PlaybackInfoResponse { PlaySessionId = "test", MediaSources = [source] };
    }
    [HttpPost("/test/live")]
    public ActionResult<LiveStreamResponse> OpenLiveStream([FromQuery] bool? enableDirectPlay, [FromQuery] bool? enableDirectStream) => new LiveStreamResponse(Source);
    [HttpPost("/test/unsupported"), ActionName("GetPostedPlaybackInfo")]
    public ActionResult<PlaybackInfoResponse> Unsupported([FromQuery] bool? enableDirectPlay, [FromQuery] bool? enableDirectStream, [FromQuery] bool? allowAudioStreamCopy) => new PlaybackInfoResponse { MediaSources = [new MediaSourceInfo { SupportsTranscoding = false }] };
}
