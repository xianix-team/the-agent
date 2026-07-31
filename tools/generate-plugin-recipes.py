#!/usr/bin/env python3
"""DEPRECATED: prefer tools/generate-agent-setup.py

Historically wrote TheAgent/Knowledge/plugin-execution-recipes.json.
That catalog is retired from runtime. Use generate-agent-setup.py to emit
per-plugin .xianix/agent-setup.json from tools/legacy-catalogs/.
"""

import json

MP = "xianix-team/plugins-official"
MN = "xianix-plugins-official"


def pref(name, slash=None):
    e = {"plugin-name": f"{name}@{MN}", "marketplace": MP}
    if slash:
        e["slash-command"] = slash
    return e


def gh_repo():
    return {"url": {"value": "https://github.com/org/repo.git", "constant": True}}


def ado_repo():
    return {"url": {"value": "https://dev.azure.com/org/project/_git/repo", "constant": True}}


def chat(slash, model="claude-sonnet-4-5", budget=5.0):
    return {"slashCommand": slash, "model": model, "max-budget-usd": budget}


def platform_block(envs_gh, envs_ado, triggers_gh, triggers_ado, gh_events, executions_gh, executions_ado):
    return {
        "github": {
            "requiredEnvs": envs_gh,
            "suggestedGitHubWebhookEvents": gh_events,
            "suggestedTriggers": triggers_gh,
            "executions": executions_gh,
        },
        "azuredevops": {
            "requiredEnvs": envs_ado,
            "suggestedTriggers": triggers_ado,
            "executions": executions_ado,
        },
    }


def gh_label_exec(name, plugin, slash, label, prompt, events="pr"):
    if events == "pr":
        matches = [
            {
                "name": f"github-{name}-tag-applied",
                "rule": f"action==labeled&&label.name=='{label}'&&pull_request.state=='open'",
            },
            {
                "name": f"github-{name}-opened-with-tag",
                "rule": f"action==opened&&pull_request.labels.*.name=='{label}'&&pull_request.state=='open'",
            },
            {
                "name": f"github-{name}-synchronize-with-tag",
                "rule": f"action==synchronize&&pull_request.labels.*.name=='{label}'&&pull_request.state=='open'",
            },
        ]
        input_name, input_path = "pr-number", "pull_request.number"
    else:
        matches = [
            {"name": f"github-{name}-tag-applied", "rule": f"action==labeled&&label.name=='{label}'"},
            {
                "name": f"github-{name}-opened-with-tag",
                "rule": f"action==opened&&issue.labels.*.name=='{label}'",
            },
        ]
        input_name, input_path = "issue-number", "issue.number"

    return {
        "name": f"github-{name}",
        "platform": "github",
        "repository": gh_repo(),
        "match-any": matches,
        "use-inputs": [{"name": input_name, "value": input_path, "mandatory": True}],
        "use-plugins": [pref(plugin, slash)],
        "with-envs": [
            {"name": "GITHUB-TOKEN", "value": "secrets.GITHUB-TOKEN", "mandatory": True},
            {"name": "ANTHROPIC-API-KEY", "value": "secrets.ANTHROPIC-API-KEY", "mandatory": True},
        ],
        "model": "claude-sonnet-4-5",
        "max-budget-usd": 5.0,
        "execute-prompt": prompt,
    }


def ado_pr_label_exec(name, plugin, slash, label, prompt):
    return {
        "name": f"azuredevops-{name}",
        "platform": "azuredevops",
        "repository": ado_repo(),
        "match-any": [
            {
                "name": f"azuredevops-{name}-tag",
                "rule": f"eventType==git.pullrequest.created&&resource.labels.*.name=='{label}'&&resource.status=='active'",
            },
            {
                "name": f"azuredevops-{name}-updated-tag",
                "rule": f"eventType==git.pullrequest.updated&&resource.labels.*.name=='{label}'&&resource.status=='active'",
            },
        ],
        "use-inputs": [{"name": "pr-number", "value": "resource.pullRequestId", "mandatory": True}],
        "use-plugins": [pref(plugin, slash)],
        "with-envs": [
            {"name": "AZURE-DEVOPS-TOKEN", "value": "secrets.AZURE-DEVOPS-TOKEN", "mandatory": True},
            {"name": "ANTHROPIC-API-KEY", "value": "secrets.ANTHROPIC-API-KEY", "mandatory": True},
        ],
        "model": "claude-sonnet-4-5",
        "max-budget-usd": 5.0,
        "execute-prompt": prompt,
    }


def ado_wi_tag_exec(name, plugin, slash, tag, prompt):
    return {
        "name": f"azuredevops-{name}",
        "platform": "azuredevops",
        "match-any": [
            {
                "name": f"azuredevops-{name}-tagged",
                "rule": f'eventType==workitem.updated&&resource.revision.fields."System.Tags"*=' + f"'{tag}'",
            },
        ],
        "use-inputs": [{"name": "workitem-id", "value": "resource.workItemId"}],
        "use-plugins": [pref(plugin, slash)],
        "with-envs": [
            {"name": "AZURE-DEVOPS-TOKEN", "value": "secrets.AZURE-DEVOPS-TOKEN", "mandatory": True},
            {"name": "ANTHROPIC-API-KEY", "value": "secrets.ANTHROPIC-API-KEY", "mandatory": True},
        ],
        "model": "claude-sonnet-4-5",
        "max-budget-usd": 5.0,
        "execute-prompt": prompt,
    }


def label_plugin(plugin, slash, gh_label, ado_label, prompt_gh, prompt_ado, kind="pr"):
    if kind == "pr":
        gh = [gh_label_exec(plugin, plugin, slash, gh_label, prompt_gh, events="pr")]
        ado = [ado_pr_label_exec(plugin, plugin, slash, ado_label, prompt_ado)]
        gh_events = ["label", "pull_request"]
        trg_gh = [f"PR label {gh_label}"]
        trg_ado = [f"PR tag {ado_label}"]
    else:
        gh = [gh_label_exec(plugin, plugin, slash, gh_label, prompt_gh, events="issue")]
        ado = [ado_wi_tag_exec(plugin, plugin, slash, ado_label, prompt_ado)]
        gh_events = ["label", "issues"]
        trg_gh = [f"Issue label {gh_label}"]
        trg_ado = [f"Work item tag {ado_label}"]
    return {
        "slashCommand": slash,
        "platforms": platform_block(
            ["GITHUB-TOKEN", "ANTHROPIC-API-KEY"],
            ["AZURE-DEVOPS-TOKEN", "ANTHROPIC-API-KEY"],
            trg_gh,
            trg_ado,
            gh_events,
            gh,
            ado,
        ),
        "chat": chat(slash),
    }


def schedule_plugin(plugin, slash, cron="15 0 1,15 * *"):
    inner_gh = {
        "name": f"github-{plugin}",
        "platform": "github",
        "repository": gh_repo(),
        "use-plugins": [pref(plugin)],
        "model": "claude-sonnet-4-5",
        "max-budget-usd": 10.0,
        "execute-prompt": (
            f"You are xianix-agent assigned for dependency health optimization. "
            f"Run {slash} to scan manifests, verify licenses, and open a remediation pull request when safe."
        ),
    }
    inner_ado = {
        "name": f"azuredevops-{plugin}",
        "platform": "azuredevops",
        "repository": ado_repo(),
        "use-plugins": [pref(plugin)],
        "model": "claude-sonnet-4-5",
        "max-budget-usd": 10.0,
        "execute-prompt": (
            f"You are xianix-agent assigned for dependency health optimization. "
            f"Run {slash} to scan manifests, verify licenses, and open a remediation pull request when safe."
        ),
    }
    return {
        "slashCommand": slash,
        "platforms": {
            "github": {
                "requiredEnvs": ["GITHUB-TOKEN", "ANTHROPIC-API-KEY"],
                "suggestedTriggers": [f"Schedule cron {cron}"],
                "executions": [
                    {
                        "schedule": f"github-{plugin}-schedule",
                        "platform": "github",
                        "cron": cron,
                        "with-envs": [
                            {"name": "GITHUB-TOKEN", "value": "secrets.GITHUB-TOKEN", "mandatory": True},
                            {
                                "name": "ANTHROPIC-API-KEY",
                                "value": "secrets.ANTHROPIC-API-KEY",
                                "mandatory": True,
                            },
                        ],
                        "executions": [inner_gh],
                    }
                ],
            },
            "azuredevops": {
                "requiredEnvs": ["AZURE-DEVOPS-TOKEN", "ANTHROPIC-API-KEY"],
                "suggestedTriggers": [f"Schedule cron {cron}"],
                "executions": [
                    {
                        "schedule": f"azuredevops-{plugin}-schedule",
                        "platform": "azuredevops",
                        "cron": cron,
                        "with-envs": [
                            {
                                "name": "AZURE-DEVOPS-TOKEN",
                                "value": "secrets.AZURE-DEVOPS-TOKEN",
                                "mandatory": True,
                            },
                            {
                                "name": "ANTHROPIC-API-KEY",
                                "value": "secrets.ANTHROPIC-API-KEY",
                                "mandatory": True,
                            },
                        ],
                        "executions": [inner_ado],
                    }
                ],
            },
        },
        "chat": chat(slash, budget=10.0),
    }


def pr_open_plugin(plugin, slash, prompt):
    gh = [
        {
            "name": f"github-{plugin}",
            "platform": "github",
            "repository": gh_repo(),
            "match-any": [
                {"name": f"github-{plugin}-opened", "rule": "action==opened&&pull_request.state=='open'"},
                {
                    "name": f"github-{plugin}-synchronize",
                    "rule": "action==synchronize&&pull_request.state=='open'",
                },
                {
                    "name": f"github-{plugin}-reopened",
                    "rule": "action==reopened&&pull_request.state=='open'",
                },
            ],
            "use-inputs": [{"name": "pr-number", "value": "pull_request.number", "mandatory": True}],
            "use-plugins": [pref(plugin, slash)],
            "with-envs": [
                {"name": "GITHUB-TOKEN", "value": "secrets.GITHUB-TOKEN", "mandatory": True},
                {"name": "ANTHROPIC-API-KEY", "value": "secrets.ANTHROPIC-API-KEY", "mandatory": True},
            ],
            "model": "claude-sonnet-4-5",
            "max-budget-usd": 5.0,
            "execute-prompt": prompt,
        }
    ]
    ado = [
        {
            "name": f"azuredevops-{plugin}",
            "platform": "azuredevops",
            "repository": ado_repo(),
            "match-any": [
                {
                    "name": f"azuredevops-{plugin}-created",
                    "rule": "eventType==git.pullrequest.created&&resource.status=='active'",
                },
                {
                    "name": f"azuredevops-{plugin}-updated",
                    "rule": "eventType==git.pullrequest.updated&&message.text*='updated the source branch'&&resource.status=='active'",
                },
            ],
            "use-inputs": [{"name": "pr-number", "value": "resource.pullRequestId", "mandatory": True}],
            "use-plugins": [pref(plugin, slash)],
            "with-envs": [
                {"name": "AZURE-DEVOPS-TOKEN", "value": "secrets.AZURE-DEVOPS-TOKEN", "mandatory": True},
                {"name": "ANTHROPIC-API-KEY", "value": "secrets.ANTHROPIC-API-KEY", "mandatory": True},
            ],
            "model": "claude-sonnet-4-5",
            "max-budget-usd": 5.0,
            "execute-prompt": prompt,
        }
    ]
    return {
        "slashCommand": slash,
        "platforms": platform_block(
            ["GITHUB-TOKEN", "ANTHROPIC-API-KEY"],
            ["AZURE-DEVOPS-TOKEN", "ANTHROPIC-API-KEY"],
            ["PR opened / synchronized / reopened"],
            ["PR created / source branch updated"],
            ["pull_request"],
            gh,
            ado,
        ),
        "chat": chat(slash),
    }


def tag_plugin(plugin, slash, prompt):
    gh = [
        {
            "name": f"github-{plugin}",
            "platform": "github",
            "repository": gh_repo(),
            "match-any": [
                {"name": f"github-{plugin}-tag", "rule": "ref_type=='tag'"},
            ],
            "use-plugins": [pref(plugin, slash)],
            "with-envs": [
                {"name": "GITHUB-TOKEN", "value": "secrets.GITHUB-TOKEN", "mandatory": True},
                {"name": "ANTHROPIC-API-KEY", "value": "secrets.ANTHROPIC-API-KEY", "mandatory": True},
            ],
            "model": "claude-sonnet-4-5",
            "max-budget-usd": 5.0,
            "execute-prompt": prompt,
        }
    ]
    ado = [
        {
            "name": f"azuredevops-{plugin}",
            "platform": "azuredevops",
            "repository": ado_repo(),
            "match-any": [
                {
                    "name": f"azuredevops-{plugin}-tag",
                    "rule": "eventType==git.push&&resource.refUpdates.*.name*='refs/tags/'",
                },
            ],
            "use-plugins": [pref(plugin, slash)],
            "with-envs": [
                {"name": "AZURE-DEVOPS-TOKEN", "value": "secrets.AZURE-DEVOPS-TOKEN", "mandatory": True},
                {"name": "ANTHROPIC-API-KEY", "value": "secrets.ANTHROPIC-API-KEY", "mandatory": True},
            ],
            "model": "claude-sonnet-4-5",
            "max-budget-usd": 5.0,
            "execute-prompt": prompt,
        }
    ]
    return {
        "slashCommand": slash,
        "platforms": platform_block(
            ["GITHUB-TOKEN", "ANTHROPIC-API-KEY"],
            ["AZURE-DEVOPS-TOKEN", "ANTHROPIC-API-KEY"],
            ["Git tag created"],
            ["Git tag push"],
            ["create", "push"],
            gh,
            ado,
        ),
        "chat": chat(slash),
    }


pr_reviewer = {
    "slashCommand": "/pr-review",
    "platforms": {
        "github": {
            "requiredEnvs": ["GITHUB-TOKEN", "ANTHROPIC-API-KEY"],
            "suggestedGitHubWebhookEvents": ["label", "pull_request", "issue_comment"],
            "suggestedTriggers": ["PR label ai-dlc/pr/pr-review", "PR comment mentioning @xianix"],
            "executions": [
                {
                    "name": "github-pull-request-review",
                    "platform": "github",
                    "repository": gh_repo(),
                    "match-any": [
                        {
                            "name": "github-pr-tag-applied",
                            "rule": "action==labeled&&label.name=='ai-dlc/pr/pr-review'&&pull_request.state=='open'",
                        },
                        {
                            "name": "github-pr-opened-with-tag",
                            "rule": "action==opened&&pull_request.labels.*.name=='ai-dlc/pr/pr-review'&&pull_request.state=='open'",
                        },
                        {
                            "name": "github-pr-synchronize-with-tag",
                            "rule": "action==synchronize&&pull_request.labels.*.name=='ai-dlc/pr/pr-review'&&pull_request.state=='open'",
                        },
                    ],
                    "use-inputs": [
                        {"name": "pr-number", "value": "pull_request.number", "mandatory": True}
                    ],
                    "use-plugins": [pref("pr-reviewer")],
                    "with-envs": [
                        {"name": "GITHUB-TOKEN", "value": "secrets.GITHUB-TOKEN", "mandatory": True}
                    ],
                    "conversation-key": "pull_request.number",
                    "model": "claude-sonnet-4-5",
                    "max-budget-usd": 3.0,
                    "execute-prompt": "You are reviewing pull request #{{pr-number}} in the repository {{repository-name}}. Run /pr-review {{pr-number}} to perform the automated review.",
                },
                {
                    "name": "github-pr-agent-comment-instruction",
                    "platform": "github",
                    "repository": gh_repo(),
                    "match-any": [
                        {
                            "name": "github-pr-agent-re-instruction-requested",
                            "rule": "action==created&&comment.body*='@xianix'&&issue.pull_request?",
                        }
                    ],
                    "use-inputs": [
                        {"name": "pr-number", "value": "issue.number"},
                        {"name": "user-instruction", "value": "comment.body"},
                        {"name": "comment-author", "value": "comment.user.login"},
                        {"name": "comment-id", "value": "comment.id"},
                    ],
                    "use-plugins": [pref("pr-reviewer", "/pr-review")],
                    "with-envs": [
                        {"name": "GITHUB-TOKEN", "value": "secrets.GITHUB-TOKEN", "mandatory": True}
                    ],
                    "model": "claude-sonnet-4-5",
                    "max-budget-usd": 5.0,
                    "execute-prompt": "You are @xianix. {{comment-author}} mentioned @xianix in a comment on pull request #{{pr-number}}. The comment: \"{{user-instruction}}\"\n\nFirst, decide whether this comment is actually addressed to you. If not, do nothing. If yes, post a reply via the platform comment API and perform any requested action. Post at most one reply comment per invocation.",
                },
            ],
        },
        "azuredevops": {
            "requiredEnvs": ["AZURE-DEVOPS-TOKEN", "ANTHROPIC-API-KEY"],
            "suggestedTriggers": [
                "PR created / branch updated / agent as reviewer",
                "PR comment mentioning @xianix",
            ],
            "executions": [
                {
                    "name": "azuredevops-pull-request-review",
                    "platform": "azuredevops",
                    "repository": ado_repo(),
                    "match-any": [
                        {
                            "name": "azuredevops-pr-created-with-tag",
                            "rule": "eventType==git.pullrequest.created&&resource.status=='active'",
                        },
                        {
                            "name": "azuredevops-pr-source-branch-updated-with-tag",
                            "rule": "eventType==git.pullrequest.updated&&message.text*='updated the source branch'&&resource.status=='active'",
                        },
                        {
                            "name": "azuredevops-pr-agent-added-as-reviewer",
                            "rule": "eventType==git.pullrequest.updated&&message.text*='changed the reviewer list'&&resource.reviewers.*.uniqueName=='xianix-agent@99x.io'&&resource.status=='active'",
                        },
                    ],
                    "use-inputs": [
                        {"name": "pr-number", "value": "resource.pullRequestId", "mandatory": True}
                    ],
                    "use-plugins": [pref("pr-reviewer")],
                    "with-envs": [
                        {
                            "name": "AZURE-DEVOPS-TOKEN",
                            "value": "secrets.AZURE-DEVOPS-TOKEN",
                            "mandatory": True,
                        }
                    ],
                    "conversation-key": "resource.pullRequestId",
                    "model": "claude-sonnet-4-5",
                    "max-budget-usd": 5.0,
                    "execute-prompt": "You are reviewing pull request #{{pr-number}} in the repository {{repository-name}}. Run /pr-review {{pr-number}} to perform the automated review.",
                },
                {
                    "name": "azuredevops-pr-agent-comment-instruction",
                    "platform": "azuredevops",
                    "repository": ado_repo(),
                    "match-any": [
                        {
                            "name": "azuredevops-pr-agent-re-review-requested",
                            "rule": "eventType==ms.vss-code.git-pullrequest-comment-event&&resource.comment.commentType=='text'&&resource.comment.content*='@xianix'",
                        }
                    ],
                    "use-inputs": [
                        {"name": "pr-number", "value": "resource.pullRequest.pullRequestId"},
                        {"name": "user-instruction", "value": "resource.comment.content"},
                        {"name": "comment-author", "value": "resource.comment.author.displayName"},
                        {"name": "thread-id", "value": "resource.comment.parentCommentId"},
                    ],
                    "use-plugins": [pref("pr-reviewer", "/pr-review")],
                    "with-envs": [
                        {
                            "name": "AZURE-DEVOPS-TOKEN",
                            "value": "secrets.AZURE-DEVOPS-TOKEN",
                            "mandatory": True,
                        }
                    ],
                    "model": "claude-sonnet-4-5",
                    "max-budget-usd": 5.0,
                    "execute-prompt": "You are @xianix. {{comment-author}} mentioned @xianix in a comment on pull request #{{pr-number}}. The comment: \"{{user-instruction}}\"\n\nFirst, decide whether this comment is actually addressed to you. If not, do nothing. If yes, post a reply via the Azure DevOps REST API and perform any requested action. Post at most one reply comment per invocation.",
                },
            ],
        },
    },
    "chat": chat("/pr-review"),
}

req_analyst = {
    "slashCommand": "/requirement-analysis",
    "platforms": {
        "github": {
            "requiredEnvs": ["GITHUB-TOKEN", "ANTHROPIC-API-KEY"],
            "suggestedGitHubWebhookEvents": ["label", "issues", "issue_comment"],
            "suggestedTriggers": [
                "Issue label ai-dlc/issue/analyze",
                "Issue comment mentioning @xianix",
            ],
            "executions": [
                {
                    "name": "github-issue-requirement-analysis",
                    "platform": "github",
                    "repository": gh_repo(),
                    "match-any": [
                        {
                            "name": "github-issue-tag-applied",
                            "rule": "action==labeled&&label.name=='ai-dlc/issue/analyze'",
                        },
                        {
                            "name": "github-issue-opened-with-tag",
                            "rule": "action==opened&&issue.labels.*.name=='ai-dlc/issue/analyze'",
                        },
                    ],
                    "use-inputs": [
                        {"name": "issue-number", "value": "issue.number", "mandatory": True}
                    ],
                    "use-plugins": [pref("req-analyst")],
                    "model": "claude-haiku-4-5",
                    "execute-prompt": "Issue #{{issue-number}} in the repository {{repository-name}} has been assigned to xianix-agent for requirement analysis.\n\nRun /requirement-analysis {{issue-number}} to perform the automated requirement analysis and elaboration.",
                },
                {
                    "name": "github-issue-agent-comment-instruction",
                    "platform": "github",
                    "repository": gh_repo(),
                    "match-any": [
                        {
                            "name": "github-issue-agent-re-instruction-requested",
                            "rule": "action==created&&comment.body*='@xianix'&&issue.pull_request!?",
                        }
                    ],
                    "use-inputs": [
                        {"name": "issue-number", "value": "issue.number"},
                        {"name": "user-instruction", "value": "comment.body"},
                        {"name": "comment-author", "value": "comment.user.login"},
                        {"name": "comment-id", "value": "comment.id"},
                    ],
                    "use-plugins": [pref("req-analyst", "/requirement-analysis")],
                    "with-envs": [
                        {"name": "GITHUB-TOKEN", "value": "secrets.GITHUB-TOKEN", "mandatory": True}
                    ],
                    "model": "claude-sonnet-4-5",
                    "max-budget-usd": 5.0,
                    "execute-prompt": "You are @xianix. {{comment-author}} mentioned @xianix in a comment on issue #{{issue-number}}. The comment: \"{{user-instruction}}\"\n\nFirst, decide whether this comment is actually addressed to you. If not, do nothing. If yes, post a reply via the platform comment API and perform any requested action. Post at most one reply comment per invocation.",
                },
            ],
        },
        "azuredevops": {
            "requiredEnvs": ["AZURE-DEVOPS-TOKEN", "ANTHROPIC-API-KEY"],
            "suggestedTriggers": ["Work item assigned to xianix-agent"],
            "executions": [
                {
                    "name": "azuredevops-work-item-requirement-analysis",
                    "platform": "azuredevops",
                    "match-any": [
                        {
                            "name": "azuredevops-workitem-assigned-to-agent",
                            "rule": 'eventType==workitem.updated&&resource.fields."System.AssignedTo".newValue==\'xianix-agent <xianix-agent@99x.io>\'&&resource.revision.fields."System.State"==\'To Do\'',
                        }
                    ],
                    "use-inputs": [{"name": "workitem-id", "value": "resource.workItemId"}],
                    "use-plugins": [pref("req-analyst")],
                    "with-envs": [
                        {
                            "name": "AZURE-DEVOPS-TOKEN",
                            "value": "secrets.AZURE-DEVOPS-TOKEN",
                            "mandatory": True,
                        },
                        {"name": "ANTHROPIC-MODEL", "value": "claude-haiku-4-5", "constant": True},
                    ],
                    "execute-prompt": "Work item #{{workitem-id}} has been assigned to xianix-agent for requirement analysis.\n\nRun /requirement-analysis {{workitem-id}} to perform the automated requirement analysis and elaboration.",
                }
            ],
        },
    },
    "chat": chat("/requirement-analysis", "claude-haiku-4-5", 3.0),
}

recipes = {
    "pr-reviewer": pr_reviewer,
    "req-analyst": req_analyst,
    "test-strategist": label_plugin(
        "test-strategist",
        "/test-strategy",
        "ai-dlc/pr/test-strategy",
        "ai-dlc/pr/test-strategy",
        "You are generating a risk-based test strategy for pull request #{{pr-number}}.\n\nRun /test-strategy pr {{pr-number}} to generate the impact analysis and test strategy report.",
        "You are generating a risk-based test strategy for pull request #{{pr-number}}.\n\nRun /test-strategy pr {{pr-number}} to generate the impact analysis and test strategy report.",
        "pr",
    ),
    "dependency-optimizer": schedule_plugin("dependency-optimizer", "/dependency-optimizer"),
    "doc-writer": label_plugin(
        "doc-writer",
        "/update-docs",
        "ai-dlc/update-docs",
        "ai-dlc/update-docs",
        "Pull request #{{pr-number}} requested documentation updates. Run /update-docs {{pr-number}} to synchronize docs.",
        "Pull request #{{pr-number}} requested documentation updates. Run /update-docs {{pr-number}} to synchronize docs.",
        "pr",
    ),
    "perf-optimizer": label_plugin(
        "perf-optimizer",
        "/perf-optimize",
        "ai-dlc/perf/optimize",
        "ai-dlc/perf/optimize",
        "Issue #{{issue-number}} requested performance optimization. Run /perf-optimize {{issue-number}}.",
        "Work item #{{workitem-id}} requested performance optimization. Run /perf-optimize {{workitem-id}}.",
        "issue",
    ),
    "arch-fitness": label_plugin(
        "arch-fitness",
        "/arch-fitness",
        "ai-dlc/arch/fitness",
        "ai-dlc/arch/fitness",
        "Issue #{{issue-number}} requested architecture fitness review. Run /arch-fitness {{issue-number}}.",
        "Work item #{{workitem-id}} requested architecture fitness review. Run /arch-fitness {{workitem-id}}.",
        "issue",
    ),
    "impact-analyst": label_plugin(
        "impact-analyst",
        "/impact-analysis",
        "ai-dlc/impact/analyze",
        "ai-dlc/impact/analyze",
        "Pull request #{{pr-number}} requested impact analysis. Run /impact-analysis pr {{pr-number}}.",
        "Pull request #{{pr-number}} requested impact analysis. Run /impact-analysis pr {{pr-number}}.",
        "pr",
    ),
    "pr-comment-resolver": label_plugin(
        "pr-comment-resolver",
        "/resolve-comments",
        "ai-dlc/pr/resolve-comments",
        "ai-dlc/pr/resolve-comments",
        "Pull request #{{pr-number}} requested comment resolution. Run /resolve-comments {{pr-number}}.",
        "Pull request #{{pr-number}} requested comment resolution. Run /resolve-comments {{pr-number}}.",
        "pr",
    ),
    "pr-descriptor": pr_open_plugin(
        "pr-descriptor",
        "/pr-describe",
        "Pull request #{{pr-number}} needs an updated description. Run /pr-describe {{pr-number}}.",
    ),
    "release-note-maintainer": tag_plugin(
        "release-note-maintainer",
        "/release-notes",
        "A new git tag was published. Run /release-notes to generate structured release notes.",
    ),
    "web-app-tester": label_plugin(
        "web-app-tester",
        "/test-web-app",
        "ai-dlc/pr/web-app-test",
        "ai-dlc/pr/web-app-test",
        "Pull request #{{pr-number}} requested web app testing. Run /test-web-app {{pr-number}}.",
        "Pull request #{{pr-number}} requested web app testing. Run /test-web-app {{pr-number}}.",
        "pr",
    ),
    "chatbot-tester": label_plugin(
        "chatbot-tester",
        "/test-chatbot",
        "ai-dlc/issue/chatbot-test",
        "ai-dlc/issue/chatbot-test",
        "Issue #{{issue-number}} requested chatbot testing. Run /test-chatbot {{issue-number}}.",
        "Work item #{{workitem-id}} requested chatbot testing. Run /test-chatbot {{workitem-id}}.",
        "issue",
    ),
    "pentest-agent": label_plugin(
        "pentest-agent",
        "/pentest",
        "ai-dlc/security/pentest",
        "ai-dlc/security/pentest",
        "Issue #{{issue-number}} authorized a pentest. Run /pentest --authorized for the target described in the issue.",
        "Work item #{{workitem-id}} authorized a pentest. Run /pentest --authorized for the target described in the work item.",
        "issue",
    ),
    "infra-scanner": label_plugin(
        "infra-scanner",
        "/infra-scan",
        "ai-dlc/security/infra-scan",
        "ai-dlc/security/infra-scan",
        "Issue #{{issue-number}} authorized infrastructure scanning. Run /infra-scan --authorized.",
        "Work item #{{workitem-id}} authorized infrastructure scanning. Run /infra-scan --authorized.",
        "issue",
    ),
    "ux-mob-process": label_plugin(
        "ux-mob-process",
        "/ux-start",
        "ai-dlc/ux/start",
        "ai-dlc/ux/start",
        "Issue #{{issue-number}} started a UX mob process. Run /ux-start and follow human-gated phases.",
        "Work item #{{workitem-id}} started a UX mob process. Run /ux-start and follow human-gated phases.",
        "issue",
    ),
}

doc = {
    "schemaVersion": 1,
    "marketplace": {
        "repo": MP,
        "marketplaceName": MN,
        "defaultMarketplaceRef": "main",
    },
    "recipes": recipes,
}

path = r"c:\99xProjectDir\Xianix\the-agent\tools\legacy-catalogs\plugin-execution-recipes.json"
with open(path, "w", encoding="utf-8") as f:
    json.dump(doc, f, indent=2)
    f.write("\n")
print("wrote", path, "plugins", len(recipes))
print("DEPRECATED: run python tools/generate-agent-setup.py next to emit per-plugin agent-setup.json")
