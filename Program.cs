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
    kernelBuilder.Plugins.AddFromPromptDirectory("plugins/clarifier");
    kernelBuilder.Plugins.AddFromPromptDirectory("plugins/decomposer");
    kernelBuilder.Plugins.AddFromPromptDirectory("plugins/summarizer");
    kernelBuilder.Plugins.AddFromPromptDirectory("plugins/combiner");
    kernelBuilder.Plugins.AddFromPromptDirectory("plugins/reviewer");
    kernelBuilder.Plugins.AddFromPromptDirectory("plugins/miniCombiner");
    kernelBuilder.Plugins.AddFromPromptDirectory("plugins/extractor");

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