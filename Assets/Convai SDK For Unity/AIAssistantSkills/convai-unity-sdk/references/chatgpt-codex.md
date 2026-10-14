# ChatGPT / Codex setup

Use the local Unity project workspace in ChatGPT desktop or Codex. The project skill lives at `.agents/skills/convai-unity-sdk/SKILL.md`; Unity's package skill directory alone is not a Codex discovery location.

In **Convai > Convai Editor > AI Coding > ChatGPT / Codex**:

1. Install the project skill. This copies the canonical skill and its references; Update refreshes it after SDK changes. Local edits require an explicit Replace choice.
2. Optionally install the project instructions in `AGENTS.md`. The managed Convai block preserves existing project instructions.
3. Open the [Unity plugin](https://chatgpt.com/plugins/plugins_6aa1c02597c081918e358d72f65bd772) in the desktop app for Unity's general workflows.
4. Open **Project Settings > AI > Unity MCP Server**, configure **Codex**, and enable the required Convai tools. The compatible Assistant package supplies this integration. Complete any connection or trust steps in the client.
5. Invoke `$convai-unity-sdk` in Codex and verify that the client discovers Convai project-status, inspection, and validation tools. If the skill is missing, reopen the project or restart the client. Use discovered schemas and tool names.

The Unity plugin, the project skill, and the Assistant MCP connection are separate. The plugin's CLI/Pipeline tools do not by themselves expose Assistant-registered `Convai_*` tools. If only generic Unity tools are available, diagnose the Assistant MCP connection before attempting Convai tool calls. Continue independent repository work while that connection is unavailable.

A copied skill does not publish a ChatGPT plugin or make a local Editor reachable from a browser-only chat. This setup targets a desktop client with access to the project and its local Unity MCP server. Never infer connection health from an installed skill or plugin.

See [OpenAI skill discovery documentation](https://learn.chatgpt.com/docs/build-skills) for project skill locations.
