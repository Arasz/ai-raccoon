# MCP stays thin

An MCP server maps its tools 1:1 onto the backend REST/API surface and holds no business logic of its own. Frontend and MCP are both clients of the same API — never let either write to the datastore directly, and never let the MCP layer branch on business rules the API doesn't already enforce.
