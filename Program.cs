using ProxyKit;
using Serilog;
using GridQuota;
using Microsoft.Extensions.Options;
using SeleniumSwissKnife;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
	.SetBasePath(Directory.GetCurrentDirectory())
	.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
	.AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
	.AddEnvironmentVariables();

// Configure Serilog
Log.Logger = new LoggerConfiguration()
	.MinimumLevel.Information()
	.ReadFrom.Configuration(builder.Configuration)
	.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
	.Enrich.FromLogContext()
	.WriteTo.Debug()
	.CreateLogger();

builder.Host.UseSerilog();

// Add services to the container.
builder.Services.Configure<AppConfig>(builder.Configuration.GetSection("app"));
builder.Services.AddControllers();
builder.Services.AddRazorPages();

builder.Services.AddProxy(httpClientBuilder => httpClientBuilder.UseSocketsHttpHandler());
builder.Services.AddSingleton<SessionProxyHandler>();
builder.Services.AddSingleton<QuotaManager>();
builder.Services.AddSingleton<WorkerPool>();
builder.Services.AddSingleton<IWorkerPool>(sp => sp.GetRequiredService<WorkerPool>());
builder.Services.AddSingleton<IWorkerRegistry>(sp => sp.GetRequiredService<WorkerPool>());
builder.Services.AddHostedService<LifecycleService>();
builder.Services.AddHttpClient();
builder.Services.AddControllers();

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILogger<AppConfig>>();

if (app.Environment.IsDevelopment())
{
	app.Use((context, next) =>
	{
		if (context.WebSockets.IsWebSocketRequest)
		{
			logger.LogDebug("(WS) WebSocket request received on path: {Path}", context.Request.Path);
		}
		return next(context);
	});
}

app.UseWebSockets();

app.UseWebSocketProxy(context =>
	{
		logger.LogDebug("WebSocket request received. Path: {Path}", context.Request.Path);

		// TODO identify target by session ID~
		var host = app.Services.GetRequiredService<IOptions<AppConfig>>().Value.Hosts.Single();
		return new Uri("ws://" + host.HostUri.Host + ":" + host.HostUri.Port);
	},
	options => options.AddXForwardedHeaders());


// app.UseSerilogRequestLogging(); // Add this line to log HTTP requests

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAuthorization();

// Forwarding /status and /ui endpoints to the first host so that general stats are available via proxy.
foreach(var path in new string[] {"/status", "/ui", "/graphql"})
{
	app.Map(path, app1 => 
	{
		app1.RunProxy(context => {
			var hostUri = context.RequestServices.GetRequiredService<IOptions<AppConfig>>().Value.Hosts.First().HostUri;
			return context.ForwardTo(new Uri(hostUri, path)).AddXForwardedHeaders().Send();
		});
	});
}

// main endpoint for processing Selenium sessions
app.Map("/session", appBuilder => appBuilder.RunProxy<SessionProxyHandler>());

// Expose our own stats.
app.MapGet("/stats", (QuotaManager manager, WorkerPool workerPool) =>
{
	var stats = new AppStatsPayload(manager.GetStats(), workerPool.GetStats());
	return Results.Json(stats);
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
	app.UseDeveloperExceptionPage();
}

app.MapControllers();
app.MapRazorPages();

logger.LogDebug("You will see Debug messages and higher");

app.Lifetime.ApplicationStarted.Register(() =>
{
	logger.LogInformation("Web server is running and bound to the following addresses:");
	foreach (var address in app.Urls)
	{
		logger.LogInformation("    {Address}", address);
	}
});


app.Lifetime.ApplicationStopping.Register(() =>
{
	logger.LogInformation("Application is stopping");
	Log.CloseAndFlush();
});

try
{
	app.Run();
	return 0;
}
catch (Exception ex)
{
	logger.LogError(ex, "Application terminated unexpectedly");
	return 1;
}

public record AppStatsPayload(QuotaStatsPayload Quota, WorkerStatsPayload Workers);
