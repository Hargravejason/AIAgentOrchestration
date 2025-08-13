using Microsoft.SemanticKernel.ChatCompletion;

namespace AIAgentOrchestration.Orchestrator.Models;

public record OrchestrationResults(bool Success, string Result, string ErrorMessage = "", OrchestrationExecutionSummary? ExecutionSummary = null)
{
  public static OrchestrationResults SuccessResult(string result, OrchestrationExecutionSummary? ExecutionSummary = null) => new OrchestrationResults(true, result, ExecutionSummary: ExecutionSummary);
  public static OrchestrationResults FailureResult(string errorMessage, OrchestrationExecutionSummary? ExecutionSummary = null) => new OrchestrationResults(false, string.Empty, errorMessage, ExecutionSummary);
};

public record OrchestrationExecutionSummary(int InputTokens, int OutputTokens, ChatHistory History, Dictionary<string, string> ModelsUsed)
{
  public int TotalTokens = InputTokens + OutputTokens;
}
