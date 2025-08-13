using AIAgentOrchestration.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

namespace AIAgentOrchestration.Core;

public static class ServiceCollectionExtensions
{
  public static IServiceCollection AddAgentKernel(this IServiceCollection services, IConfiguration config)
  {
    var kernelOptions = config.GetSection("AIAgents").Get<List<KernalAgentConfig>>();

    // Add the chat completion service(s)
    if(kernelOptions == null || kernelOptions.Count == 0)
      return services; // No agents configured, return without adding services

    return services.AddAgentKernel(kernelOptions);
  }

  public static IServiceCollection AddAgentKernel(this IServiceCollection services, List<KernalAgentConfig> kernelConfig)
  {
    if (kernelConfig == null || kernelConfig.Count == 0)
      return services; // No agents configured, return without adding services

    services.AddTransient<Kernel>(x =>
    {
      // Initialize a Kernel with a chat-completion service
      IKernelBuilder builder = Kernel.CreateBuilder();

      // Add the chat completion service(s)
      foreach (var item in kernelConfig)
      {
        switch (item.KernelType)
        {
          case KernelType.AzureOpenAI:
            builder.AddAzureOpenAIChatCompletion(
              deploymentName: item.Model,
              apiKey: item.ApiKey,
              endpoint: item.EndPoint,
              serviceId: item.ServiceId
            );
            break;
          case KernelType.Ollama:
            #pragma warning disable SKEXP0070
            builder.AddOllamaChatCompletion(
              modelId: item.Model,
              endpoint: new Uri(item.EndPoint),
              serviceId: item.ServiceId
            );
            break;
          default:
            break;
        }
      }
      return builder.Build();
    });

    return services;
  }
}
