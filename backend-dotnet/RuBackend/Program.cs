using RuBackend.Compatibility;
using RuBackend.Configuration;
using RuBackend.Security;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<BackendOptions>(
    builder.Configuration.GetSection(BackendOptions.SectionName));
builder.Services.AddHttpClient<IUpstreamHttpTransport, HttpUpstreamTransport>();
builder.Services.AddSingleton<HttpForwarder>();
builder.WebHost.UseUrls(
    builder.Configuration["RuBackend:BindUrl"] ?? "http://127.0.0.1:8010");

var app = builder.Build();

app.UseMiddleware<DesktopTokenMiddleware>();
app.UseMiddleware<RequestSizeMiddleware>();

app.MapMethods("/{**path}", new[] { HttpMethods.Options }, () => Results.NoContent());
app.MapMethods(
    "/status",
    new[] { HttpMethods.Get },
    (HttpContext context, HttpForwarder forwarder, CancellationToken cancellationToken) =>
        forwarder.ForwardAsync(context, cancellationToken));
app.MapMethods(
    "/settings",
    new[] { HttpMethods.Get, HttpMethods.Post },
    (HttpContext context, HttpForwarder forwarder, CancellationToken cancellationToken) =>
        forwarder.ForwardAsync(context, cancellationToken));

app.Run();

public partial class Program { }
