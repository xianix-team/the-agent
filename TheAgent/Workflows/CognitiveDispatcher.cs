using Microsoft.Extensions.Logging;
using Temporalio.Workflows;
using Xianix.Rules.Schedule;
using Xians.Lib.Agents.Core;

namespace Xianix.Workflows;

[Workflow]
public sealed class CognitiveDispatcher
{
    private readonly ScheduleEvaluator _scheduleEvaluator;
    public CognitiveDispatcher()
    {
        _scheduleEvaluator = new ScheduleEvaluator();
    }
    [WorkflowRun]
    public async Task RunAsync()
    {
        Workflow.Logger.LogDebug("Cognitive Dispatcher started for tenant '{TenantId}'.", XiansContext.TenantId);

        try
        {
            foreach (ScheduleEntry schedule in await _scheduleEvaluator.Evaluate())
            {
                await XiansContext.CurrentAgent.Schedules
                .Create<JobDispatcherWorkflow>(schedule.ScheduleName)
                .WithCronSchedule(schedule.cronExpression, timezone: schedule.timezone)
                .WithInput(schedule)
                .CreateIfNotExistsAsync();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Workflow.Logger.LogError(ex, "Tenant {TenantId}: rules evaluation threw an exception.", XiansContext.TenantId);
        }
    }
}
