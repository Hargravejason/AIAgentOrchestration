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
  public static IServiceCollection AddAssistantAgent(this IServiceCollection services)
  {
    services.AddTransient<IKernalAgent>(x =>
    {
      var configuration = x.GetRequiredService<IConfiguration>();
      Kernel kernel = x.GetRequiredService<Kernel>();

      ChatCompletionAgent aiAssistant = new ChatCompletionAgent
      {
        Name = "Assistant",
        Description = "A helpful AI assistant.",
        Instructions = "You are a helpful AI assistant that helps users answer questions the best you can. You have a variety of co-workers that can help you get the answers for the user if you do not know the answer. If a co-worker exists for a specific topic, make sure you hand it over to them to answer the question so you can answer other users questions.",
        Kernel = kernel,
        InstructionsRole = AuthorRole.Assistant,
        Arguments = new KernelArguments(new PromptExecutionSettings() { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(), ExtensionData = new Dictionary<string, object> { { "temperature", "0.4" } } })
      };

      return new KernalAgent() { Agent = aiAssistant, Name = "AI Assistant", Order =-1, Type = AgentType.Handoff };
    });

    return services;
  }
}