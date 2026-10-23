using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Sellora.NotificationService.Api.Authorization;
using Sellora.NotificationService.Api.Identity;
using Sellora.NotificationService.Application.Identity;
using Sellora.NotificationService.Api.Tenancy;
using Sellora.NotificationService.Application.Dispatch;
using Sellora.NotificationService.Application.Notifications;
using Sellora.NotificationService.Domain.Tenancy;
using Sellora.NotificationService.Infrastructure.Dispatch;
using Sellora.NotificationService.Infrastructure.Email;
using Sellora.NotificationService.Infrastructure.Kafka;
using Sellora.NotificationService.Infrastructure.Notifications;
using Sellora.NotificationService.Infrastructure.Persistence;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// Same JWT setup as every Sellora service (WSO2 IS).
var jwt = builder.Configuration.GetSection("Jwt");
var audiences = jwt.GetSection("Audience").Get<string[]>()
    ?? new[] { jwt["Audience"]! };

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = jwt["Authority"];
        options.MetadataAddress = jwt["MetadataAddress"]!;
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt["Issuer"],
            ValidateAudience = true,
            ValidAudiences = audiences,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = "roles"
        };

        // Local and staging hosts may not trust the shared WSO2 CA; the
        // relaxation is limited to the identity provider's own host.
        if (builder.Environment.IsDevelopment() || builder.Environment.IsStaging())
        {
            options.BackchannelHttpHandler = IdentityProviderBackchannel.CreateHandler(jwt["MetadataAddress"]);
        }
    });

builder.Services.AddAuthorization(options => options.AddSelloraRolePolicies());

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<HttpTenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<HttpTenantContext>());
builder.Services.AddScoped<ISystemTenantContext>(sp => sp.GetRequiredService<HttpTenantContext>());
builder.Services.AddSingleton(TimeProvider.System);

var connectionString = builder.Configuration.GetConnectionString("Default");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("The notification database connection string is not configured.");
}

builder.Services.AddDbContext<NotificationDbContext>(options => options.UseNpgsql(connectionString));

// US-E5-1: consume order events into Pending notification requests.
builder.Services.Configure<OrderEventConsumerOptions>(builder.Configuration.GetSection(OrderEventConsumerOptions.SectionName));
builder.Services.AddScoped<INotificationIntake, NotificationIntakeService>();
builder.Services.AddScoped<INotificationRequestReader, NotificationRequestReader>();

// US-E5-2: render once, send the same message to the shop and the agency concurrently.
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));
builder.Services.Configure<DispatchOptions>(builder.Configuration.GetSection(DispatchOptions.SectionName));
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<INotificationDispatcher, NotificationDispatcher>();

// US-E5-3: admin resend (and the caller it records).
builder.Services.AddScoped<ICallerIdentity, HttpCallerIdentity>();
builder.Services.AddScoped<INotificationResendService, NotificationResendService>();

// US-E5-4: the company alert address used for low-stock notifications.
builder.Services.AddScoped<INotificationSettingsService, NotificationSettingsService>();

if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<OrderEventConsumerService>();
    builder.Services.AddHostedService<NotificationDispatchService>();
}

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddControllers();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Azure terminates TLS before forwarding requests to this container.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins);
        }

        policy.AllowAnyHeader().AllowAnyMethod();
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseExceptionHandler();
app.UseSerilogRequestLogging();
app.UseForwardedHeaders();

// Migrate before the consumer can write (hosted services start after this line runs).
if (!app.Environment.IsEnvironment("Testing"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
    await db.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Containers receive internal HTTP; HTTPS is terminated by APIM / Azure.
if (!app.Environment.IsEnvironment("Container"))
{
    app.UseHttpsRedirection();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.MapGet("/whoami", (HttpContext context) =>
    Results.Ok(context.User.Claims.Select(claim => new { claim.Type, claim.Value })))
    .RequireAuthorization();

await app.RunAsync();

public partial class Program
{
    protected Program()
    {
    }
}
