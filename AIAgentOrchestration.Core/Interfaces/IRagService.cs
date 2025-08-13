using AIAgentOrchestration.Core.Models;

namespace AIAgentOrchestration.Core.Interfaces;

public interface IRagService
{
  Task<List<DocumentChunk>> RetrieveRelevantChunksAsync(string query, AgentType domain, CancellationToken ct = default);
}
