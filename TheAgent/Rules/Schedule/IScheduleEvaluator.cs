namespace Xianix.Rules.Schedule;

public interface IScheduleEvaluator
{
    Task<List<ScheduleEntry>> Evaluate();
}
