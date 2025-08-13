using Microsoft.SemanticKernel;

namespace AIAgentOrchestration.Core.KernelExtensions;

public static class KernelBuilderExtensions
{
  public static IKernelBuilder AddDefaultPlugins(this IKernelBuilder builder)
  {
    // Add standard plugins: Time, Math, Web, etc.
    return builder;
  }
}