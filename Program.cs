using Npgsql;
using RnxInternetShop.Components;
using RnxInternetShop.Data;
using RnxInternetShop.Messaging;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents();
builder.Services.AddHttpContextAccessor();
builder.Services.AddDataProtection();
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("Postgres")!));
builder.Services.AddSingleton<Catalog>();
builder.Services.AddSingleton<Orders>();
builder.Services.AddScoped<ShopSession>();
builder.Services.Configure<MailOptions>(builder.Configuration.GetSection("Mail"));
builder.Services.AddOptions<SmsOptions>().BindConfiguration("Sms")
    .Validate(options => options.Webhook is null ||
        (options.Webhook.IsAbsoluteUri && options.Webhook.Scheme is "https" or "http"),
        "Sms:Webhook must be an absolute HTTP or HTTPS URI.")
    .ValidateOnStart();
builder.Services.AddHttpClient<OrderEffects>(client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHostedService<OrderWorker>();

var app = builder.Build();
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/error", createScopeForErrors: true);

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        if (context.Response.ContentType?.StartsWith("text/html", StringComparison.Ordinal) == true)
            context.Response.Headers.CacheControl = "no-store";
        return Task.CompletedTask;
    });
    await next(context);
});
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>();

await ShopDatabase.InitializeAsync(app.Services.GetRequiredService<NpgsqlDataSource>(),
    app.Lifetime.ApplicationStopping);
await app.RunAsync();
