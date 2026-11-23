using System.Text.Json.Serialization;
using Hangfire;
using Hangfire.MemoryStorage;
using MeetingActions.Api;
using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using MeetingActions.Infrastructure.Integrations;
using MeetingActions.Infrastructure.Persistence;
using MeetingActions.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var provider = builder.Configuration["Persistence:Provider"] ?? "Sqlite";
if (!string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
{
    throw new NotSupportedException($"Persistence provider '{provider}' is not installed. Add its EF Core provider and configure it here.");
}

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");
builder.Services.AddDbContext<MeetingActionsDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddScoped<IMeetingRepository, MeetingRepository>();
builder.Services.AddScoped<ActionItemStateMachine>();
builder.Services.AddScoped<FollowUpService>();
builder.Services.AddTransient<ILlmExtractor, StubLlmExtractor>();
builder.Services.Configure<AzureOpenAiOptions>(builder.Configuration.GetSection("AzureOpenAI"));
builder.Services.AddTransient<AzureOpenAiExtractor>();
builder.Services.Configure<AzureDevOpsOptions>(builder.Configuration.GetSection("AzureDevOps"));
builder.Services.Configure<TeamsWebhookOptions>(builder.Configuration.GetSection("Teams"));
builder.Services.AddTransient<INotifier, ConsoleNotifier>();

var taskProvider = builder.Configuration["TaskProvider:Type"] ?? "AzureDevOps";
if (string.Equals(taskProvider, "MockJira", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ITaskProvider, MockJiraProvider>();
}
else if (string.Equals(taskProvider, "AzureDevOps", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddTransient<ITaskProvider, AzureDevOpsProvider>();
}
else
{
    throw new InvalidOperationException($"Unknown task provider '{taskProvider}'.");
}

builder.Services.AddSingleton<VirtualClock>();
builder.Services.AddSingleton<IClock>(services => services.GetRequiredService<VirtualClock>());
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5173", "http://127.0.0.1:5173"];
builder.Services.AddCors(options => options.AddPolicy("meeting-actions-ui", policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred.",
            Instance = context.HttpContext.Request.Path
        };
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        return new BadRequestObjectResult(problem);
    };
});
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier);
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHangfire(configuration => configuration.UseMemoryStorage());
builder.Services.AddHangfireServer();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<MeetingActionsDbContext>();
    await dbContext.Database.MigrateAsync();
    await SeedData.InitializeAsync(scope.ServiceProvider, app.Lifetime.ApplicationStopping);
}

app.Lifetime.ApplicationStarted.Register(() => RecurringJob.AddOrUpdate<FollowUpService>(
    "meeting-actions-follow-up",
    service => service.RunAsync(CancellationToken.None),
    Cron.Daily));

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("meeting-actions-ui");
app.UseExceptionHandler();
app.MapControllers();
app.Run();

/// <summary>Exposes the top-level API entry point to integration tests.</summary>
public partial class Program { }
