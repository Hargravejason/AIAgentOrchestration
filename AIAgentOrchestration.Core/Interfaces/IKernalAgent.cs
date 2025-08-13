using AIAgentOrchestration.Core.Models;
using Microsoft.SemanticKernel.Agents;

namespace AIAgentOrchestration.Core.Interfaces;

public interface IKernalAgent
{
  string Name { get; }
  AgentType Type { get; }

  ICollection<ChatCompletionAgent> GetAgents();
}
