using AIAgentOrchestration.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel;
using Microsoft.Extensions.Configuration;
using AIAgentOrchestration.Core.Models;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AIAgentOrchestration.News;

public static class ServiceCollectionExtensions
{
  public static IServiceCollection AddDeveloperAgent(this IServiceCollection services)
  {
    services.AddScoped<IKernalAgent>( x =>
    {
      var configuration = x.GetRequiredService<IConfiguration>();
      Kernel kernel = x.GetRequiredService<Kernel>();

      ChatCompletionAgent srDeveloperAgent = new ChatCompletionAgent
      {
        Name = "SrDeveloper",
        Description = "A Sr. Developer Agent",
        Instructions = "You are a Sr. Software Developer with over 15 years’ experience. You specialize in Microsoft .Net stack. You value simplicity and functionality over bloated code and overengineering. You are very good at debugging the toughest code problems and are the ‘go-to guy’ when it comes to development questions. Security is also at the forefront of your development practices, always thinking of how others might try to harm your applications.",
        Kernel = kernel,
        InstructionsRole = AuthorRole.Assistant,
        Arguments = new KernelArguments(new PromptExecutionSettings() { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(), ExtensionData = new Dictionary<string, object> { { "temperature", "0.3" } } })
      };

      return new KernalAgent() { Agent = srDeveloperAgent, Name = "Sr Developer", Order = 1, Type = AgentType.Handoff, HandoffReason = "Transfer to this agent if the issue is not software development related" };
    });

    services.AddScoped<IKernalAgent>(x =>
    {
      var configuration = x.GetRequiredService<IConfiguration>();
      Kernel kernel = x.GetRequiredService<Kernel>();

      ChatCompletionAgent srDeveloperAgent = new ChatCompletionAgent
      {
        Name = "SrUIDeveloper",
        Description = "A Sr. UI/UX Developer Agent",
        Instructions = "You are a Sr. UI/UX Software Developer with over 10 years’ experience. You have been making websites, desktop, and mobile applications look great for the better part of a decade. You are passionate about the user experience and ensuring that their interactions are elegant and enjoyable. You have an eye for color combinations and what always seems to be the best way to present information on screen. You also have a strong background in JavaScript, as well as other front-end technologies such as JQuery, Angular, React. And UI frameworks like Bootstrap. You have also worked with Blazor and Microsoft MAUI.",
        Kernel = kernel,
        InstructionsRole = AuthorRole.Assistant,
        Arguments = new KernelArguments(new PromptExecutionSettings() { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(), ExtensionData = new Dictionary<string, object> { { "temperature", "0.3" } } })
      };

      return new KernalAgent() { Agent = srDeveloperAgent, Name = "Sr Developer", Order = 2, Type = AgentType.Handoff, HandoffReason = "Transfer to this agent if the issue is not software development UI or UX related" };
    });

    return services;
  }
}