using RuBackend.Configuration;
using RuBackend.Security;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<BackendOptions>(
    builder.Configuration.GetSection(BackendOptions.SectionName));
builder.WebHost.UseUrls(
    builder.Configuration["RuBackend:BindUrl"] ?? "http://127.0.0.1:8010");

var app = builder.Build();

app.UseMiddleware<DesktopTokenMiddleware>();
app.UseMiddleware<RequestSizeMiddleware>();

app.MapMethods("/{**path}", new[] { HttpMethods.Options }, () => Results.NoContent());
app.MapGet("/status", () => Results.Ok(new
{
    status = "ok",
    message = "Backend is running and ready for the WinUI3 UI.",
}));

app.Run();

public partial class Program { }
