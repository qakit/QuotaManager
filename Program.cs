using ProxyKit;
using Serilog;
using GridQuota;
using Microsoft.Extensions.Options;

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
builder.Services.AddSingleton<SessionHandler>();
builder.Services.AddSingleton<QuotaManager>();
builder.Services.AddSingleton<IWorkerPool>(sp => sp.GetRequiredService<QuotaManager>()); ;
builder.Services.AddSingleton<IWorkerRegistry>(sp => sp.GetRequiredService<QuotaManager>());
builder.Services.AddHostedService<LifecycleService>();
builder.Services.AddHttpClient();
builder.Services.AddControllers();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
	app.Use((context, next) =>
	{
		if (context.WebSockets.IsWebSocketRequest)
		{
			Log.Debug("(WS) WebSocket request received on path: {Path}", context.Request.Path);
		}
		return next(context);
	});
}

app.UseWebSockets();

app.UseWebSocketProxy(context =>
{
	Log.Debug("WebSocket request received. Path: {Path}, Protocol: {Protocol}, Origin: {Origin}",
		context.Request.Path, context.WebSockets.WebSocketRequestedProtocols, context.Request.Headers["Origin"]);

	var host = app.Services.GetRequiredService<IOptions<AppConfig>>().Value.Hosts.Single();
	var hostUri = new Uri(host.HostUri);

	return new Uri("ws://" + hostUri.Host + ":" + hostUri.Port);
},
	options => options.AddXForwardedHeaders());


app.UseSerilogRequestLogging(); // Add this line to log HTTP requests

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAuthorization();

app.Map("/session", sessionHandler => sessionHandler.RunProxy<SessionHandler>());

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
	app.UseDeveloperExceptionPage();
}

app.MapControllers();
app.MapRazorPages();

app.Lifetime.ApplicationStarted.Register(() =>
{
	Log.Information("Web server is running and bound to the following addresses:");
	foreach (var address in app.Urls)
	{
		Log.Information("    {Address}", address);
	}
});


app.Lifetime.ApplicationStopping.Register(() =>
{
	Log.Information("Application is stopping");
	Log.CloseAndFlush();
});

try
{
	app.Run();
	return 0;
}
catch (Exception ex)
{
	Log.Fatal(ex, "Application terminated unexpectedly");
	return 1;
}
