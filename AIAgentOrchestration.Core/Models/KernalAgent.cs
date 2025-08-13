using AIAgentOrchestration.Core.Interfaces;
using Microsoft.SemanticKernel.Agents;

namespace AIAgentOrchestration.Core.Models;

public class KernalAgent : IKernalAgent
{
  public string Name { get; set; }
  public int Order { get; set; }
  public AgentType Type { get; set; } = AgentType.GroupChat;
  public string HandoffReason { get; set; }
  public ChatCompletionAgent Agent { get; set; }

  public ICollection<ChatCompletionAgent> GetAgents()
  {
    throw new NotImplementedException();
  }
}

