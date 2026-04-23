using Temporalio.Workflows;
using Microsoft.Extensions.Logging;
using Xianix;
using Temporalio.Exceptions;
using Xianix.Rules.Schedule;
using Xians.Lib.Agents.Core;
using Xianix.Workflows;
using Xianix.Orchestrator;

namespace Xianix.Dispatcher;

[Workflow(Constants.AgentName + ":Job Dispatcher Workflow")]
public class JobDispatcherWorkflow
{
    [WorkflowRun]
    public async Task WorkflowRun(string tenantId, ScheduleEntry scheduleEntry)
    {
        try
        {
            OrchestrationResult orchestrationResult = OrchestrationResult.Matched(
                scheduleEntry.ScheduleName,
                tenantId,
                scheduleEntry.Inputs,
                execution: new ExecutionSpec(scheduleEntry.Plugins, scheduleEntry.Prompt)
            );
            await XiansContext.Workflows.StartAsync<ProcessingWorkflow>(new object[] { orchestrationResult }, Guid.NewGuid().ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Workflow.Logger.LogError(ex, "DispatcherWorkflow failed fatally.");
            throw new ApplicationFailureException($"Dispatcher workflow failed: {ex.Message}", ex, nonRetryable: true);
        }
    }
}