using AIAgentOrchestration.Core;
using AIAgentOrchestration.Orchestrator;
using AIAgentOrchestration.Orchestrator.Orchestration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;


var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
builder.Logging.AddDebug();
builder.Logging.SetMinimumLevel(LogLevel.Information);

//Make sure we can read the secrets
builder.Configuration.AddUserSecrets<Program>();

var httpClient = new HttpClient();

//services
builder.Services.AddAgentKernel(builder.Configuration);
//builder.Services.AddAssistantAgent();
//builder.Services.AddDeveloperAgent();
builder.Services.AddAgentOrchestration();

//build app
var app = builder.Build();

//get services
var orchestrator = app.Services.GetRequiredService<AgentOrchestrator>();

