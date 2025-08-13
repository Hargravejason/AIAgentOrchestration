namespace AIAgentOrchestration.Core.Models;

public class UserInput
{
  public string Text { get; set; } = string.Empty;
  public Dictionary<string, string>? AdditionalMetadata { get; set; }
}
