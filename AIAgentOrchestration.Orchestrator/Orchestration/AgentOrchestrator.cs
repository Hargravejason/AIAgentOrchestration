using AIAgentOrchestration.Core.Interfaces;
using AIAgentOrchestration.Core.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.Agents.Orchestration.Handoff;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration.Sequential;
using AIAgentOrchestration.Orchestrator.Models;
using Microsoft.Extensions.Logging;
using AngleSharp.Browser.Dom;

namespace AIAgentOrchestration.Orchestrator.Orchestration;

#pragma warning disable SKEXP0110
#pragma warning disable SKEXP0001
public class AgentOrchestrator
{
  private readonly ILogger<AgentOrchestrator> _logger;
  private ChatHistoryAgentThread _thread = new ChatHistoryAgentThread();

  public AgentOrchestrator(ILogger<AgentOrchestrator> logger)
  {
    _logger = logger;
  }

  public async Task<OrchestrationResults> RouteAndExecuteSequential(UserInput input, ICollection<Agent> agents, CancellationToken cancellationToken = default)
  {
    int totalPrompt = 0, totalCompletion = 0;
    Dictionary<string,string> modelsUsed = new();
    ChatHistory history = [];
    try
    {
      ValueTask responseCallback(ChatMessageContent response)
      {
        history.Add(response);

        if (response.Metadata is Dictionary<string, object> metadata && metadata.TryGetValue("Usage", out var usageObj) && usageObj is OpenAI.Chat.ChatTokenUsage usage)
        {
          totalPrompt += usage.InputTokenCount;
          totalCompletion += usage.OutputTokenCount;
        }
        if(response.InnerContent is OpenAI.Chat.ChatCompletion openAICompletion)
        {
          if(modelsUsed.ContainsKey(response.AuthorName ?? "Unknown") && !modelsUsed[response.AuthorName ?? "Unknown"].Contains(openAICompletion.Model))
            modelsUsed[response.AuthorName ?? "Unknown"] += $", {openAICompletion.Model}";
          else if(!modelsUsed.ContainsKey(response.AuthorName ?? "Unknown"))
            modelsUsed.Add(response.AuthorName ?? "Unknown", openAICompletion.Model);
        }
        return ValueTask.CompletedTask;
      }

      _thread = new ChatHistoryAgentThread(history);

      var orchestration = new SequentialOrchestration(agents.ToArray())
      {
        ResponseCallback = responseCallback,
        //StreamingResponseCallback = streamingResponseCallback
      };

      InProcessRuntime runtime = new InProcessRuntime();
      await runtime.StartAsync(cancellationToken);

      var result = await orchestration.InvokeAsync(input.Text, runtime, cancellationToken);
      string output = await result.GetValueAsync(TimeSpan.FromSeconds(240), cancellationToken);

      await runtime.RunUntilIdleAsync();

      //update the thread with the history... have to do this to get the history out of it, Orchestration doesnt support history yet.
      _thread = new ChatHistoryAgentThread(history);

      return OrchestrationResults.SuccessResult(output, new OrchestrationExecutionSummary(totalPrompt, totalCompletion, history, modelsUsed));
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, $"Error in {nameof(RouteAndExecuteSequential)}");
      return OrchestrationResults.FailureResult(ex.Message, new OrchestrationExecutionSummary(totalPrompt, totalCompletion, history, modelsUsed));
    }
  }

  public async Task RouteAndExecuteHandoff(UserInput input, CancellationToken cancellationToken = default)
  {
    ChatHistory history = [];
    _thread = new ChatHistoryAgentThread(history);

    #pragma warning disable SKEXP0110
    //GroupChatOrchestration orchestration = new GroupChatOrchestration(
    //  new RoundRobinGroupChatManager { MaximumInvocationCount = 5 }, _agents.Where(x=>x.Type == AgentType.GroupChat).Select(x=>x.Agent).ToArray())
    //  {
    //    ResponseCallback = responseCallback,
    //  };

    //var primaryAgent = _agents.OrderBy(x => x.Order).First();
    //var remainingAgents = _agents.Where(x => x != primaryAgent).OrderBy(x => x.Order).ToList();

    //var handoffs = OrchestrationHandoffs
    //  .StartWith(primaryAgent.Agent)
    //  .Add(primaryAgent.Agent, remainingAgents.Select(x => x.Agent).ToArray());

    //foreach (var item in remainingAgents)
    //  handoffs.Add(item.Agent, primaryAgent.Agent, item.HandoffReason);

    //HandoffOrchestration orchestration = new HandoffOrchestration(
    //handoffs, _agents.OrderBy(x => x.Order).Select(x=>x.Agent).ToArray())
    //{
    //  InteractiveCallback = interactiveCallback,
    //  ResponseCallback = responseCallback,
    //  StreamingResponseCallback = streamingResponseCallback,
    //};

    //InProcessRuntime runtime = new InProcessRuntime();
    //await runtime.StartAsync();

    //Console.Write($"\nAssistant: ");
    //var result = await orchestration.InvokeAsync(input.Text, runtime, cancellationToken);
    //string output = await result.GetValueAsync(TimeSpan.FromSeconds(30));

    //await runtime.RunUntilIdleAsync();

    //Console.WriteLine($"\n# RESULT: {output}");
    //Console.WriteLine("\n\nORCHESTRATION HISTORY");
    //foreach (ChatMessageContent message in history)
    //{
    //  // Print each message
    //  #pragma warning disable SKEXP0001
    //  Console.WriteLine($"# {message.Role} - {message.AuthorName}: {message.Content}");
    //}
  }

  ValueTask<ChatMessageContent> interactiveCallback()
  {
    string input = "";
    Console.WriteLine($"\n# INPUT: {input}\n");
    return ValueTask.FromResult(new ChatMessageContent(AuthorRole.User, input));
  }



}
