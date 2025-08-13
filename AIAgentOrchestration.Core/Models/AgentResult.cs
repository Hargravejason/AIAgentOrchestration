namespace AIAgentOrchestration.Core.Models;

public class AgentResult
{
  public string Output { get; set; } = string.Empty;
  public AgentType AgentUsed { get; set; }
  public List<string>? SourceDocuments { get; set; } // optional RAG citations
  public TimeSpan Duration { get; set; }
}