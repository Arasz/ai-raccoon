# Rider MCP Tool Categories

42 tools organized by tag and intent. Use this to pick the right Rider tool without scanning all 42 definitions.
Tags come from the ai-badger closed taxonomy (`features/common/mcp-tags.json`).

## Code Search & Navigation

| Tool | Tags | Intent |
|---|---|---|
| `search_symbol` | semantic, search, csharp, typescript | Find a class, method, or field by name fragment using semantic lookup |
| `search_text` | search, csharp, typescript | Full-text substring search across project files with match coordinates |
| `search_regex` | search, csharp, typescript | Regex search across project files with match coordinates |
| `search_file` | search, files | Find files by glob pattern in the project |
| `search_in_files_by_text` | search, csharp, typescript | Substring search using IntelliJ's indexed engine — faster than search_text for large codebases |
| `search_in_files_by_regex` | search, csharp, typescript | Regex search using IntelliJ's indexed engine — faster than search_regex for large codebases |
| `find_files_by_glob` | search, files | Recursive glob file search with exclusion and subdirectory scoping |
| `find_files_by_name_keyword` | search, files | Fast filename keyword lookup via internal index — fastest option for name-only searches |
| `get_symbol_info` | semantic, csharp, typescript | Get Quick Documentation, type info, and declaration for a symbol at a file position |

**When to use which search:**

| Task | Best tool |
|---|---|
| Find a class/method by name | `search_symbol` |
| Full-text search for a string in code | `search_in_files_by_text` (fastest for large codebases) |
| Regex search in code | `search_in_files_by_regex` |
| Find files by name pattern | `find_files_by_name_keyword` (fastest) |
| Find files by path glob | `search_file` |
| Get type info at cursor position | `get_symbol_info` |

## File Operations

| Tool | Tags | Intent |
|---|---|---|
| `read_file` | read, csharp, typescript | Read a file from project, dependencies, JARs, or URLs with 1-indexed line numbers |
| `get_file_text_by_path` | read | Read a file's text content by project-relative path with truncation control |
| `get_file_problems` | diagnostic, csharp, typescript | Check a file for Rider code analysis errors, warnings, and suggestions |
| `create_new_file` | write, files | Create a new file in the project directory with optional initial content |
| `replace_text_in_file` | write, files | Find and replace text in a file with case-sensitivity, regex, and replace-all options |
| `apply_patch` | write, files | Apply a Codex-format or unified git diff patch to project files |
| `open_file_in_editor` | navigation | Open a file in the JetBrains IDE editor tab |
| `reformat_file` | refactoring, csharp, typescript | Reformat a file using the solution's code style settings |
| `get_all_open_file_paths` | files, navigation | List all currently open editor file paths relative to project root |

## Refactoring

| Tool | Tags | Intent |
|---|---|---|
| `rename_refactoring` | refactoring, csharp, typescript | Rename a symbol and update all references across the entire solution |
| `move_type_to_namespace` | refactoring, csharp | Move a type to another namespace and update all references across the solution |

## Build & Run

| Tool | Tags | Intent |
|---|---|---|
| `build_solution` | dotnet, build, csharp | Compile the solution or specific files and return build status and errors |
| `execute_run_configuration` | run, dotnet, csharp | Run a configuration or code location with launch overrides and wait for exit |
| `get_run_configurations` | run, dotnet, csharp | List project run configurations or discover executable entry points in a file |
| `execute_terminal_command` | terminal | Execute a shell command in the IDE's integrated terminal with output capture |

## Project & Directory

| Tool | Tags | Intent |
|---|---|---|
| `get_solution_projects` | dotnet, csharp, files | List all projects in the currently opened solution |
| `get_project_dependencies` | dotnet, csharp | Get NuGet package references and project-to-project dependencies |
| `list_directory_tree` | files, navigation | Show a tree view of a directory in the project |
| `get_repositories` | files | List VCS repositories configured in the project |

## Database / SQL (10 tools)

| Tool | Tags | Intent |
|---|---|---|
| `list_database_connections` | database, sql | List all configured database connections |
| `test_database_connection` | database, sql | Check if a connection is valid and reachable |
| `list_database_schemas` | database, sql | List schemas in a connection |
| `list_schema_objects` | database, sql | List objects (tables, views, routines) in a schema |
| `list_schema_object_kinds` | database, sql | List supported object kinds for a connection |
| `get_database_object_description` | database, sql | Get columns, types, keys, and indexes for an object |
| `preview_table_data` | database, sql | Preview rows as CSV |
| `execute_sql_query` | database, sql | Run SQL and return results as CSV |
| `list_recent_sql_queries` | database, sql | List recent and running queries |
| `cancel_sql_query` | database, sql | Cancel a running query by session ID |

## Observability / OpenTelemetry

| Tool | Tags | Intent |
|---|---|---|
| `get_services` | tracing, opentelemetry | List all discovered OTel services |
| `get_service_map` | tracing, opentelemetry | Generate architectural service map from traces |
| `get_log_records` | tracing, opentelemetry, diagnostic | Query logs by service, severity, message, and time |
| `get_spans` | tracing, opentelemetry, diagnostic | Query spans by service, trace ID, span ID, and time |
