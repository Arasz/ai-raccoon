---
description: 'MCP server transport, API-client, and tool-contract requirements.'
applyTo: '**/*Mcp*/**'
---

# MCP Server

- Treat the MCP server as a thin API client: do not place domain, persistence, orchestration, or authorization logic here.
- Reserve stdout exclusively for the stdio MCP protocol when using stdio transport. Configure all diagnostic logging for stderr (or an out-of-band sink for HTTP transport); never write protocol-adjacent diagnostics to stdout.
- Keep every tool and parameter description accurate, specific, and useful to an LLM caller; these descriptions are the agent-facing API contract.
- Keep MCP tools mapped 1:1 to backend API operations. Business branching, persistence decisions, or state-machine logic here are defects.
- Read credentials and function/API keys only from configuration or environment variables. Do not add credentials, defaults, or sample secret values to tracked files.
- Build, test, and run the MCP server locally when the change can be exercised that way.
