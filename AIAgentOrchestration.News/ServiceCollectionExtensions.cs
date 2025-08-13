using AIAgentOrchestration.Core.Interfaces;
using AIAgentOrchestration.Core.Models;
using AIAgentOrchestration.SharedPlugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;


namespace AIAgentOrchestration.News;

public class NewsKernelAgent
{
  private readonly Kernel _kernel;
  private readonly ILogger<NewsKernelAgent> _logger;
  private readonly WebSearchPlugin _webSearchPlugin;
  private readonly WebBrowserPlugin _webBrowserPlugin;

  public string Name => "NewsAgent";

  public AgentType Type => AgentType.Sequential;

  public NewsKernelAgent(ILogger<NewsKernelAgent> logger, ILogger<WebSearchPlugin> searchLogger, ILogger<WebBrowserPlugin> browserLogger, IConfiguration configuration, Kernel kernel)
  {
    _kernel = kernel;
    _logger = logger;
    _webSearchPlugin = new WebSearchPlugin(configuration, searchLogger);
    _webBrowserPlugin = new WebBrowserPlugin(configuration, browserLogger);
  }


  public ChatCompletionAgent GetNewsFormatAgent(string serviceId = "Default")
  {
    ChatCompletionAgent formatAgent = new ChatCompletionAgent
    {
      Name = "FormatEditor",
      Description = "An agent that reviews and formats extracted property data.",
      Instructions =
      """
      You are a property research editor. Your task is to transform a list of incident summaries into a strict JSON object.

      JSON Output Rules:
      Output must be JSON only. Do not include code blocks (e.g., no ```json markup).
      Wrap everything inside a single root key: results: [].
      Format each result as a JSON object with:
      IncidentDate: string in "MM-DD-YYYY" or "N/A" if unknown
      Confidencescore: integer between 0 and 100
      Score must reflect confidence the incident occurred at the requested address, based on the content and specificity (e.g. "Chartres Street" is less confident than "1024 Chartres").
      DataSource: domain name only (e.g. "fox8live.com")
      SourceURL: full URL string
      AdditionalInfo: brief string summary of the incident
      Stigma: short label of the incident (e.g., "Fire", "Murder", "Injury")
      StigmaType: one of: "DIH", "FIRE", "Other"

      Logic:
      Remove duplicate incidents. If two sources refer to the same incident (same stigma type and same date/address), choose the more complete or dated one.
      Set Confidencescore = 100 only if the incident specifically names or strongly implies it occurred at the target address (e.g., 1024 Chartres).
      Use Confidencescore = 0 for general neighborhood mentions or unrelated information.
      For ambiguous or partial references (e.g., just “Chartres Street”), use a lower score (e.g., 60–80).
      If Stigma is "N/A" or incident is about general praise/history, use Confidencescore = 0 and set "StigmaType": "Other".

      Output:
      JSON object only. No text, markdown, or explanation.
      """,
      Kernel = _kernel.Clone(),
      InstructionsRole = AuthorRole.Assistant,
      Arguments = new KernelArguments(new PromptExecutionSettings() { ServiceId = serviceId, FunctionChoiceBehavior = FunctionChoiceBehavior.None(), ExtensionData = new Dictionary<string, object> { { "temperature", "0.2" } } }),
    };

    return formatAgent;
  }

  public ChatCompletionAgent GetNewsResearchAgent(string serviceId = "Default")
  {
    ChatCompletionAgent researchAgent = new ChatCompletionAgent
    {
      Name = "Analyst",
      Description = "An agent that gathers news and information on an address.",
      Instructions =
      """
      You are a property research analyst. Given an address you will research the following:
        - look up any news or data that might adversely impact the sale of a property, such as, but not limited to, deaths, floods, fires, hoarders, drug raids, rape, etc.
        - look up any news or data that might positively impact the sale of a property
      
      You will use the following tools to assist you in your research:
        WebSearch - performs google searches to find news articles and information about the property.
        WebBrowser - web browser for url to gather more information about the search results.
            
      Ensure the result occurred at the address, include the part of the address that is in the article in the summary. Also ensure you do not return the same incident more than once. If the search results summary does not provide enough information, browse to the url and download the web page to learn more. 
      
      Return each result by iteself, not combined: 
      1. {Property address}
          * Incident Date: {date of the incident, if available, in MM-DD-YYYY format, e.g., "5-3-2025" or "2-19-2013" or "N/A" if not available}
            - Source: {name of the data source, such as a website or news outlet, e.g., "potomaclocal.com" or "strangeoutdoors.com"}
            - Source URL: {URL of the article or incident}
            - Summary: {summary of the article or incident, 3 to 4 sentences long, the summary should be from the search/website article text not an interpretation}
      """,
      Kernel = _kernel.Clone(),
      InstructionsRole = AuthorRole.Assistant,
      Arguments = new KernelArguments(new PromptExecutionSettings() { ServiceId = serviceId, FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(), ExtensionData = new Dictionary<string, object> { { "temperature", "0.3" } } })
    };

    researchAgent.Kernel.Plugins.Add(KernelPluginFactory.CreateFromObject(_webSearchPlugin));
    researchAgent.Kernel.Plugins.Add(KernelPluginFactory.CreateFromObject(_webBrowserPlugin));

    return researchAgent;
  }

  public ChatCompletionAgent GetNewsStigmaAgent(string serviceId = "Default")
  {
    ChatCompletionAgent researchAgent = new ChatCompletionAgent
    {
      Name = "StigmaAnalyst",
      Description = "An agent that gathers reviews article information and determins how to classify it.",
      Instructions =
      """
      You are a property research analyst. You will be provided with a property data that describes an incident or event. Your task is to classify the incident into one of the following categories:
        - DIH (Death, Injury, or Harm at the property) 
        - FIRE (Fire incident at the property)
        - FLOOD (Flood incident at the property)
        - HOARDING (Hoarding incident at the property)
        - METH (Drug-related incident at the property)
        - Other (any other type of incident that does not fall under the above)

      Return your results in an outline format ONLY(multiple property addresses may exist): 
      1. {Property address}
          * Incident Date: {previously provided}
            - Source: {previously provided}
            - Source URL: {previously provided}
            - Summary: {previously provided}
            - Stigma: {the stigma, e.g., "Death", "Murder", "Fire", "Flood", "Hoarding", "Drug lab", ect...}
            - StigmaType: {the stigma type, e.g., "DIH", "FIRE", "FLOOD", "HOARDING", "METH", or "Other"}

      """,
      Kernel = _kernel.Clone(),
      InstructionsRole = AuthorRole.Assistant,
      Arguments = new KernelArguments(new PromptExecutionSettings() { ServiceId = serviceId, FunctionChoiceBehavior = FunctionChoiceBehavior.None(), ExtensionData = new Dictionary<string, object> { { "temperature", "0.2" } } })
    };

    return researchAgent;
  }
}