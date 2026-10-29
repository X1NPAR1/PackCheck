namespace PackCheck.App.Ai;

/// <summary>How much the assistant may do without asking.</summary>
public enum AssistantMode
{
    /// <summary>Every change waits for approval.</summary>
    Normal,

    /// <summary>Only looks around and answers with a plan; nothing is changed until the plan is accepted.</summary>
    Plan,

    /// <summary>File edits are applied right away (with a backup); commands, start/stop and new servers still ask.</summary>
    Auto,

    /// <summary>Everything is applied without asking.</summary>
    Bypass,
}

public static class AssistantPolicy
{
    /// <summary>Tools that change something; everything else only reads.</summary>
    public static bool Changes(string tool) => tool is "write_file" or "send_command" or "start_server" or "stop_server" or "create_servers";

    /// <summary>What happens with a changing tool call in this mode.</summary>
    public static ToolDecision Decide(AssistantMode mode, string tool) => !Changes(tool) ? ToolDecision.Run : mode switch
    {
        AssistantMode.Plan => ToolDecision.Block,
        AssistantMode.Bypass => ToolDecision.Run,
        AssistantMode.Auto when tool == "write_file" => ToolDecision.Run,
        _ => ToolDecision.Ask,
    };

    /// <summary>Instruction added to each user message so the model knows the rules of the current mode.</summary>
    public static string Instruction(AssistantMode mode) => mode switch
    {
        AssistantMode.Plan => "[Plan mode] Investigate with read-only tools only (list, read, console, validation). Do not call write_file, send_command, start_server, stop_server or create_servers. Finish with a numbered, concrete plan of the changes you would make (files, exact values, commands).",
        AssistantMode.Auto => "[Auto mode] File edits are applied immediately with a backup. Commands, starting or stopping servers and creating servers still need approval.",
        AssistantMode.Bypass => "[Bypass mode] Every change you make is applied immediately without approval. Be careful and explain what you changed.",
        _ => "[Normal mode] Every change waits for the user's approval.",
    };
}

public enum ToolDecision
{
    Run,
    Ask,
    Block,
}
