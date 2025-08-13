using AIAgentOrchestration.Orchestrator.Orchestration;
using Microsoft.Extensions.DependencyInjection;
namespace AIAgentOrchestration.Orchestrator;

public static class ServiceCollectionExtensions
{
  public static IServiceCollection AddAgentOrchestration(this IServiceCollection services)
  {
    services.AddTransient<AgentOrchestrator>();

    return services;
  }
}