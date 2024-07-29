using ProxyKit;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables();

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .Enrich.FromLogContext()
    .Enrich.WithProperty("SourceContext", "")
    .WriteTo.Debug()
    .CreateLogger();

builder.Host.UseSerilog();

// Add services to the container.
//builder.Services.Configure<RouterConfig>(builder.Configuration.GetSection("router"));
builder.Services.AddControllers();
builder.Services.AddRazorPages();

builder.Services.AddProxy();
// builder.Services.AddSingleton<SessionHandler>();
// builder.Services.AddSingleton<ILoadBalancer, RoundRobinBalancer>();
// builder.Services.AddHostedService<BalancerLifecycleService>();
builder.Services.AddHttpClient();
builder.Services.AddControllers();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseSerilogRequestLogging(); // Add this line to log HTTP requests

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAuthorization();

app.MapGet("/hello", () => "Hello World!");
app.MapControllers();
app.MapRazorPages();

app.Lifetime.ApplicationStopping.Register(() =>
{
    Log.Information("Application is stopping");
    Log.CloseAndFlush();
});

try
{
    Log.Information("Starting web application");
    app.Run();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    return 1;
}
