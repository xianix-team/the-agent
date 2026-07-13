using Temporalio.Workflows;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;
using Xianix.Rules.Schedule;
using Xians.Lib.Agents.Core;
using Xianix.Orchestrator;
using Xianix.Rules;

namespace Xianix.Workflows;

[Workflow]
public class JobDispatcherWorkflow
{
    [WorkflowRun]
    public async Task WorkflowRun(ScheduleEntry scheduleEntry)
    {
        try
        {
            foreach (WebhookExecution execution in scheduleEntry.Executions)
            {
                var inputs = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (!string.IsNullOrEmpty(execution.Platform))
                    inputs["platform"] = execution.Platform;
                if (!string.IsNullOrEmpty(execution.Repository?.Url?.Value.ToString()))
                    inputs["repository-url"] = execution.Repository.Url.Value.ToString();
                if (!string.IsNullOrEmpty(execution.Repository?.Name?.Value.ToString()))
                    inputs["repository-name"] = execution.Repository.Name.Value.ToString();

                execution.WithEnvs.AddRange(scheduleEntry.EnvVars);

                ProcessingRequest request = new ProcessingRequest()
                {
                    Name = scheduleEntry.ScheduleName,
                    Type = ProcessingType.Schedule,
                    TenantId = XiansContext.TenantId,
                    Inputs = inputs,
                    Execution = new ExecutionSpec(
                        execution.Plugins,
                        execution.Prompt,
                        execution.WithEnvs,
                        execution.Platform,
                        execution.Repository?.Url?.Value.ToString() ?? string.Empty,
                        execution.Repository?.Name?.Value.ToString() ?? string.Empty,
                        execution.Model,
                        execution.MaxTurns,
                        execution.AllowedTools,
                        execution.DisallowedTools,
                        execution.MaxBudgetUsd,
                        execution.ResumeSessions
                    ),
                };
                await XiansContext.Workflows.StartAsync<ProcessingWorkflow>(new object[] { request }, Guid.NewGuid().ToString());

            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Workflow.Logger.LogError(ex, "DispatcherWorkflow failed fatally.");
            throw new ApplicationFailureException($"Dispatcher workflow failed: {ex.Message}", ex, nonRetryable: true);
        }
    }
}