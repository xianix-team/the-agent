namespace Xianix;

public static class Constants
{
    public const string AgentName = "Xianix AI-DLC Agent";
    public const string RulesKnowledgeName = "Rules";
    public const string SystemPromptKnowledgeName = "System Prompt";
    public const string OnboardingSystemPromptKnowledgeName = "Rules Optimizer System Prompt";

    /// <summary>
    /// Messaging scope / Studio topic id for the Rules Optimizer chat.
    /// When <c>UserMessageContext.Message.Scope</c> equals this value, the supervisor
    /// loads the onboarding prompt and onboarding tools instead of the general chat flow.
    /// Studio displays the scope string as the topic name.
    /// </summary>
    public const string ProjectOnboardingScope = "Rules Optimizer";

    /// <summary>
    /// Legacy scope id from before the topic was renamed. Still treated as Rules Optimizer
    /// so existing chats keep working.
    /// </summary>
    public const string LegacyProjectOnboardingScope = "project-onboarding";
}