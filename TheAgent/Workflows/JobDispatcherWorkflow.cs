using Temporalio.Workflows;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;
using Xianix.Rules.Schedule;
using Xians.Lib.Agents.Core;
using Xianix.Orchestrator;

namespace Xianix.Workflows;

[Workflow]
public class JobDispatcherWorkflow
{
    [WorkflowRun]
    public async Task WorkflowRun(ScheduleEntry scheduleEntry)
    {
        try
        {
            ProcessingRequest request = new ProcessingRequest(){
                Name = scheduleEntry.ScheduleName,
                Type = ProcessingType.Schedule,
                TenantId = XiansContext.TenantId,
                Inputs = scheduleEntry.Inputs,
                Execution = new ExecutionSpec(scheduleEntry.Plugins, scheduleEntry.Prompt, withEnvs: scheduleEntry.EnvVars),
            };
            await XiansContext.Workflows.StartAsync<ProcessingWorkflow>(new object[] { request }, Guid.NewGuid().ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Workflow.Logger.LogError(ex, "DispatcherWorkflow failed fatally.");
            throw new ApplicationFailureException($"Dispatcher workflow failed: {ex.Message}", ex, nonRetryable: true);
        }
    }
}