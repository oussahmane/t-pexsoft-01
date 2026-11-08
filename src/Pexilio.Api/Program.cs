using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Pexilio.Api;
using Pexilio.Api.Endpoints;
using Pexilio.Api.Health;
using Pexilio.Infrastructure;
using Pexilio.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Authorization", LogLevel.Warning);

builder.Services.AddPexilio(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new OptionalTextConverter());
    options.SerializerOptions.Converters.Add(new CustomerStatementLineConverter());
});
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemAuthorizationMiddlewareResultHandler>();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<PexilioDbContext>("database");
builder.Services.AddOpenApi();

const string DesktopDevCors = "desktop-dev";
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options =>
    {
        options.AddPolicy(DesktopDevCors, policy =>
            policy.WithOrigins(
                    "http://localhost:1420",
                    "http://127.0.0.1:1420",
                    "http://tauri.localhost",
                    "tauri://localhost")
                .AllowAnyHeader()
                .AllowAnyMethod());
    });
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseCors(DesktopDevCors);
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = HealthResponseWriter.WriteAsync,
});
app.MapPexilioEndpoints();
app.Run();

public partial class Program;
