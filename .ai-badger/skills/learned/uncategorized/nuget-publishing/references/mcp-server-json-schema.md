# .mcp/server.json registry-schema validation (2026-08-05, ai-raccoon)

Case: after publishing (web-uploaded) arasz.ai-raccoon 1.0.1, VS Code's MCP
config generator refused to create an entry: "The VS Code MCP server configuration
entry cannot be generated because the server.json file is invalid." The package
README was useless for diagnosis — the truth was in the schema.

## The validation recipe

```bash
curl -s https://static.modelcontextprotocol.io/schemas/2025-10-17/server.schema.json -o /tmp/mcp-schema.json
python3 -c "
import json, jsonschema
schema = json.load(open('/tmp/mcp-schema.json'))   # top level is \$ref -> #/definitions/ServerDetail
doc = json.load(open('src/AiRaccoon/.mcp/server.json'))
errors = sorted(jsonschema.Draft7Validator(schema).iter_errors(doc), key=lambda e: list(e.path))
for e in errors:
    print('-', '/'.join(str(p) for p in e.path), '->', e.message[:120])
"
```

`pip install jsonschema` if missing. Collect ALL errors — the first is rarely the
only one.

## The violations found (all 4 at once)

1. `description` — 152 chars > maxLength 100:
   "Failed validating 'maxLength' in schema['properties']['description']"
   Fix: <= 100 chars. Real fix: "Agent memory over MCP: project-scoped memory
   bank, workspace sandboxes, hybrid search, cloud sync." (98 chars).
2. `packages/0/environmentVariables/0` — '"AIRACCOON_DB_PASSPHRASE" is not of
   type object' (×3, once per subschema in the allOf chain). Items must be
   KeyValueInput objects.
3. (latent) `repository.url` = https://github.com/ai-raccoon/ai-raccoon —
   nonexistent org, pre-existing typo. Passes format-uri validation, so the
   schema won't catch it; a contract test must.

## Schema field shapes (ServerDetail / Package / KeyValueInput)

- `ServerDetail.description`: maxLength 100, minLength 1.
- `ServerDetail.name`: reverse-DNS `^[a-zA-Z0-9.-]+/[a-zA-Z0-9._-]+$`, 3..200.
- `ServerDetail.version`: string, maxLength 255; ranges rejected.
- `Package` required: registryType, identifier, transport.
- `Package.environmentVariables[]`: KeyValueInput = object with REQUIRED `name`;
  optional fields from `Input`: `description`, `isSecret` (boolean), `isRequired`
  (boolean), `choices[]`, `default`, `placeholder`, `format` (enum), `value`,
  `variables`. NOTE the field names: `isSecret`/`isRequired`, NOT `secret`/`required`.
- `Repository` required: url (format uri), source. Optional: id (GitHub repo id
  via `gh api repos/<owner>/<repo> --jq '.id'`), subfolder.
- Top-level `$schema` should be the server.schema.json URI (format uri).

## Shipping the fix

- A registered id+version cannot be re-uploaded (nuget.org 409). If the bad
  server.json shipped as X.Y.Z, keep X.Y.Z unlisted and ship X.Y.Z+1 — CI pushes
  are listed by default, so the fixed version becomes the findable one.
- Pin the constraints in the version-contract test (description length, env-var
  object shape with non-empty name, repository.url) so a future edit can't ship
  an invalid file again (TDD: fact added first, 3 failing -> all green).
