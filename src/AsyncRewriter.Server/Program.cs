using AsyncRewriter.Analyzer;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Server.Repositories;
using AsyncRewriter.Server.Services;
using AsyncRewriter.Transformation;
using Microsoft.Build.Locator;

// Register MSBuild - must be done before any MSBuildWorkspace is created
MSBuildLocator.RegisterDefaults();

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Async Rewriter API",
        Version = "v1",
        Description = "C# Roslyn-based API for transforming synchronous code to async with call graph analysis"
    });
});

// Register services
builder.Services.AddSingleton<ICallGraphAnalyzer, CallGraphAnalyzer>();
builder.Services.AddSingleton<ICallGraphRepository, InMemoryCallGraphRepository>();
builder.Services.AddSingleton<IAsyncFloodingAnalyzer, AsyncFloodingAnalyzer>();
builder.Services.AddScoped<IAsyncTransformer, AsyncTransformer>();

// Register job service and background worker
builder.Services.AddSingleton<IJobService, JobService>();
builder.Services.AddHostedService<AnalysisBackgroundService>();

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthorization();
app.MapControllers();

app.Run();
