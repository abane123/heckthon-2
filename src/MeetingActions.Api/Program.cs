using System.Text.Json.Serialization;
using Hangfire;
using Hangfire.MemoryStorage;
using MeetingActions.Api;
using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using MeetingActions.Infrastructure;
using MeetingActions.Infrastructure.Integrations;
using MeetingActions.Infrastructure.Llm;
using MeetingActions.Infrastructure.Persistence;
using MeetingActions.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

MongoSerialization.Register();
builder.Services.AddOptions<MongoOptions>()
    .Bind(builder.Configuration.GetSection(MongoOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString)
        && IsValidMongoConnectionString(options.ConnectionString),
        "MongoDB:ConnectionString must be a valid MongoDB connection string.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.DatabaseName),
        "MongoDB:DatabaseName is required.")
    .ValidateOnStart();
builder.Services.AddSingleton<IMongoClient>(services =>
{
    var options = services.GetRequiredService<IOptions<MongoOptions>>().Value;
    var settings = MongoClientSettings.FromConnectionString(options.ConnectionString);
    settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
    return new MongoClient(settings);
});
builder.Services.AddSingleton<MongoContext>();
builder.Services.AddSingleton<IMongoDatabaseInitializer, MongoDatabaseInitializer>();
builder.Services.AddScoped<IMeetingRepository, MongoMeetingRepository>();
builder.Services.AddScoped<IActionItemRepository, MongoActionItemRepository>();
builder.Services.AddScoped<IUserRepository, MongoUserRepository>();
builder.Services.AddScoped<IFollowUpRepository, MongoFollowUpRepository>();
builder.Services.AddScoped<IAuditEventRepository, MongoAuditEventRepository>();
builder.Services.AddScoped<IDemoDataStore, MongoDemoDataStore>();
builder.Services.AddScoped<IPersistenceHealthCheck, MongoPersistenceHealthCheck>();
builder.Services.AddScoped<ActionItemStateMachine>();
builder.Services.AddScoped<FollowUpService>();
builder.Services.AddInfrastructure(builder.Configuration);
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

var llmSettings = app.Services.GetRequiredService<IOptions<LlmOptions>>().Value;
var activeModel = llmSettings.Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
    ? app.Services.GetRequiredService<IOptions<OllamaOptions>>().Value.Model
    : "Built-in rules";
app.Logger.LogInformation("LLM provider {Provider} selected with model {Model}.", llmSettings.Provider, activeModel);

try
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<IMongoDatabaseInitializer>()
        .InitializeAsync(app.Lifetime.ApplicationStopping);
    await SeedData.InitializeAsync(
        scope.ServiceProvider.GetRequiredService<IDemoDataStore>(),
        scope.ServiceProvider.GetRequiredService<ILlmExtractor>(),
        scope.ServiceProvider.GetRequiredService<IClock>(),
        app.Lifetime.ApplicationStopping);
}
catch (Exception exception) when (exception is MongoException or TimeoutException or LlmUnavailableException)
{
    app.Logger.LogError(
        exception,
        "Startup seeding could not reach a configured dependency; the API will start with the available services.");
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

/// <summary>Checks that configured database endpoints use a syntactically valid MongoDB URI.</summary>
static bool IsValidMongoConnectionString(string connectionString)
{
    try
    {
        _ = MongoUrl.Create(connectionString);
        return true;
    }
    catch (MongoConfigurationException)
    {
        return false;
    }
    catch (ArgumentException)
    {
        return false;
    }
}

/// <summary>Exposes the top-level API entry point to integration tests.</summary>
public partial class Program { }
