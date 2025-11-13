using AIAgentOrchestration.Core;
using AIAgentOrchestration.Orchestrator;
using AIAgentOrchestration.Orchestrator.Orchestration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PdfSkeleton;


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

string pdfFilePath = "C:\\Users\\hargr\\Downloads\\scanned_document_example.pdf";

byte[] pdfBytes = await File.ReadAllBytesAsync(pdfFilePath);

//var pdfParser = new MuPdfCorePlainTextParser();
//var text = pdfParser.Parse(pdfBytes);

GC.Collect(0);

//text = pdfParser.Parse(pdfBytes);

var cancel = false;
// Add this before the loop to start a background task for key detection
var keyListenerTask = Task.Run(() =>
{
    while (!cancel)
    {
        if (Console.KeyAvailable)
        {
            cancel = true;
            break;
        }
        Thread.Sleep(100);
    }
});

while (!cancel)
{
  await Task.Delay(500);
  // Your loop logic here
  GC.Collect();
}
