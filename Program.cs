using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using DeepResearcher.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using DeepResearcher;
using Microsoft.OpenApi.Models;

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Add services to the container
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    // Add memory cache for session storage
    builder.Services.AddMemoryCache();

    // Configure and add Semantic Kernel
    var config = builder.Configuration;
    var kernelBuilder = Kernel.CreateBuilder();

    // Register all the plugins
    string basePath = Path.Combine(builder.Environment.ContentRootPath, "plugins");

    //kernelBuilder.Plugins.AddFromPromptDirectory(basePath+"/clarifier");
    //kernelBuilder.Plugins.AddFromPromptDirectory(basePath + "/decomposer");
    //kernelBuilder.Plugins.AddFromPromptDirectory(basePath + "/summarizer");
    //kernelBuilder.Plugins.AddFromPromptDirectory(basePath + "/combiner");
    //kernelBuilder.Plugins.AddFromPromptDirectory(basePath + "/reviewer");
    //kernelBuilder.Plugins.AddFromPromptDirectory(basePath + "/miniCombiner");
    //kernelBuilder.Plugins.AddFromPromptDirectory(basePath + "/extractor");
    string[] pluginDirectories = { "clarifier", "decomposer", "summarizer", "combiner", "reviewer", "miniCombiner", "extractor" };
    
    foreach (var dir in pluginDirectories)
    {
        string pluginPath = Path.Combine(basePath, dir);
        if (Directory.Exists(pluginPath))
        {
            kernelBuilder.Plugins.AddFromPromptDirectory(pluginPath);
            
        }
        else
        {
        }
    }
    // Add OpenAI service
    kernelBuilder.AddAzureOpenAIChatCompletion(
        deploymentName: config["OpenAI:ChatDeployment"]!,
        endpoint: config["OpenAI:Endpoint"]!,
        apiKey: config["OpenAI:Key"]!
    );

    var kernel = kernelBuilder.Build();
    builder.Services.AddSingleton(kernel);

    // Add TavilyConnector
    var tavilyApiKey = config["Tavily:ApiKey"];
    if (string.IsNullOrEmpty(tavilyApiKey))
    {
        throw new InvalidOperationException("Tavily API key not configured in appsettings.json");
    }

    builder.Services.AddSingleton(new TavilyConnector(tavilyApiKey));

    // Add research service
    builder.Services.AddScoped<IResearchService, ResearchService>();

    // Add ResearchCache
    builder.Services.AddSingleton<ResearchCache>();
    
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
    
    // Configure Swagger
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "DeepResearcher API",
            Version = "1.0",
            Description = "API for DeepResearcher Agent"
        });
    });
    
    // Disable Azure blob trace listener that's causing errors
    builder.Logging.AddFilter("Microsoft.WindowsAzure.WebSites.Diagnostics.AzureBlobTraceListener", LogLevel.None);

    var app = builder.Build();
    
    // Enable CORS
    app.UseCors();
    app.UseSwagger();
    app.UseSwaggerUI();

    app.UseHttpsRedirection();
    app.UseAuthorization();
    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"\nError: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
    Console.ResetColor();
}