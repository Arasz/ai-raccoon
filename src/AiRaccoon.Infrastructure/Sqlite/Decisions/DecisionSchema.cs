using System.Text.RegularExpressions;

namespace AiRaccoon.Infrastructure.Sqlite.Decisions;

/// <summary>Project decision tables in the encrypted bank, excluded from automatic cloud sync.</summary>
internal static class DecisionSchema
{
    internal static IEnumerable<string> TableNames => Regex.Matches(Ddl, @"CREATE (?:VIRTUAL )?TABLE IF NOT EXISTS (\w+)")
        .Select(match => match.Groups[1].Value);

    internal static IEnumerable<string> ProjectTableNames => Regex.Matches(Ddl, @"CREATE TABLE IF NOT EXISTS (\w+)")
        .Select(match => match.Groups[1].Value);

    internal const string Ddl = """
        CREATE TABLE IF NOT EXISTS decision_projects (
            project_id TEXT PRIMARY KEY NOT NULL CHECK(project_id <> 'shared'),
            revision INTEGER NOT NULL DEFAULT 0 CHECK(revision >= 0)
        );
        CREATE TABLE IF NOT EXISTS decision_tasks (
            project_id TEXT NOT NULL, task_id TEXT NOT NULL, title TEXT NOT NULL,
            registered_order INTEGER NOT NULL CHECK(registered_order > 0), registered_at TEXT NOT NULL, source_ref TEXT NULL,
            PRIMARY KEY(project_id,task_id), UNIQUE(project_id,registered_order),
            FOREIGN KEY(project_id) REFERENCES decision_projects(project_id)
        );
        CREATE TABLE IF NOT EXISTS decisions (
            project_id TEXT NOT NULL, decision_id TEXT NOT NULL, origin_task_id TEXT NOT NULL,
            current_version INTEGER NOT NULL CHECK(current_version > 0), created_at TEXT NOT NULL,
            PRIMARY KEY(project_id,decision_id),
            FOREIGN KEY(project_id,origin_task_id) REFERENCES decision_tasks(project_id,task_id)
        );
        CREATE INDEX IF NOT EXISTS idx_decisions_origin ON decisions(project_id,origin_task_id,decision_id);
        CREATE TABLE IF NOT EXISTS decision_versions (
            project_id TEXT NOT NULL, decision_id TEXT NOT NULL, version INTEGER NOT NULL CHECK(version > 0),
            task_id TEXT NOT NULL, project_revision INTEGER NOT NULL CHECK(project_revision > 0),
            title TEXT NOT NULL, category TEXT NOT NULL, scenario TEXT NOT NULL, outcome TEXT NOT NULL, reasoning TEXT NOT NULL,
            alternatives TEXT NOT NULL CHECK(json_valid(alternatives)), consequences TEXT NOT NULL CHECK(json_valid(consequences)),
            confidence REAL NULL CHECK(confidence >= 0 AND confidence <= 1), decision_maker TEXT NULL,
            status TEXT NOT NULL CHECK(status IN ('Proposed','Accepted','Rejected','Superseded','Deprecated')),
            valid_from TEXT NULL, valid_until TEXT NULL, recorded_at TEXT NOT NULL, actor TEXT NOT NULL, session_id TEXT NOT NULL,
            change_reason TEXT NULL CHECK((version=1 AND change_reason IS NULL) OR (version>1 AND change_reason IS NOT NULL AND length(trim(change_reason))>0)),
            sources TEXT NOT NULL CHECK(json_valid(sources)),
            PRIMARY KEY(project_id,decision_id,version), UNIQUE(project_id,project_revision,decision_id),
            FOREIGN KEY(project_id,decision_id) REFERENCES decisions(project_id,decision_id),
            FOREIGN KEY(project_id,task_id) REFERENCES decision_tasks(project_id,task_id)
        );
        CREATE INDEX IF NOT EXISTS idx_decision_versions_task ON decision_versions(project_id,task_id,project_revision);
        CREATE TABLE IF NOT EXISTS decision_edges (
            project_id TEXT NOT NULL, edge_id TEXT NOT NULL, source_id TEXT NOT NULL, target_id TEXT NOT NULL,
            kind TEXT NOT NULL CHECK(kind IN ('DependsOn','Supersedes','Supports','Contradicts')), reason TEXT NOT NULL,
            task_id TEXT NOT NULL, created_revision INTEGER NOT NULL CHECK(created_revision > 0), created_at TEXT NOT NULL,
            actor TEXT NOT NULL, session_id TEXT NOT NULL,
            removed_revision INTEGER NULL, removed_at TEXT NULL, removal_task_id TEXT NULL,
            removal_reason TEXT NULL, removal_actor TEXT NULL, removal_session_id TEXT NULL,
            CHECK(source_id <> target_id),
            CHECK((removed_revision IS NULL AND removed_at IS NULL AND removal_task_id IS NULL AND removal_reason IS NULL AND removal_actor IS NULL AND removal_session_id IS NULL)
               OR (removed_revision IS NOT NULL AND removed_revision > created_revision AND removed_at IS NOT NULL AND removal_task_id IS NOT NULL AND removal_reason IS NOT NULL AND removal_actor IS NOT NULL AND removal_session_id IS NOT NULL AND kind <> 'Supersedes')),
            PRIMARY KEY(project_id,edge_id),
            FOREIGN KEY(project_id,source_id) REFERENCES decisions(project_id,decision_id),
            FOREIGN KEY(project_id,target_id) REFERENCES decisions(project_id,decision_id),
            FOREIGN KEY(project_id,task_id) REFERENCES decision_tasks(project_id,task_id),
            FOREIGN KEY(project_id,removal_task_id) REFERENCES decision_tasks(project_id,task_id)
        );
        CREATE UNIQUE INDEX IF NOT EXISTS uq_decision_live_edge ON decision_edges(project_id,source_id,target_id,kind) WHERE removed_revision IS NULL;
        CREATE INDEX IF NOT EXISTS idx_decision_edges_source ON decision_edges(project_id,source_id,kind);
        CREATE INDEX IF NOT EXISTS idx_decision_edges_target ON decision_edges(project_id,target_id,kind);
        CREATE TABLE IF NOT EXISTS decision_requests (
            project_id TEXT NOT NULL, request_id TEXT NOT NULL, canonical_payload_hash TEXT NOT NULL,
            result TEXT NOT NULL CHECK(json_valid(result)), committed_revision INTEGER NOT NULL CHECK(committed_revision > 0),
            PRIMARY KEY(project_id,request_id), FOREIGN KEY(project_id) REFERENCES decision_projects(project_id)
        );
        CREATE TABLE IF NOT EXISTS decision_imports (
            project_id TEXT NOT NULL, source_namespace TEXT NOT NULL, external_id TEXT NOT NULL, decision_id TEXT NULL,
            task_id TEXT NOT NULL, source_digest TEXT NOT NULL, raw_payload TEXT NOT NULL CHECK(json_valid(raw_payload)),
            mapping TEXT NOT NULL CHECK(json_valid(mapping)), PRIMARY KEY(project_id,source_namespace,external_id),
            FOREIGN KEY(project_id,task_id) REFERENCES decision_tasks(project_id,task_id),
            FOREIGN KEY(project_id,decision_id) REFERENCES decisions(project_id,decision_id)
        );
        CREATE TABLE IF NOT EXISTS decision_search (
            id INTEGER PRIMARY KEY, project_id TEXT NOT NULL, decision_id TEXT NOT NULL,
            title TEXT NOT NULL, category TEXT NOT NULL, scenario TEXT NOT NULL, outcome TEXT NOT NULL, reasoning TEXT NOT NULL,
            UNIQUE(project_id,decision_id),
            FOREIGN KEY(project_id,decision_id) REFERENCES decisions(project_id,decision_id)
        );
        CREATE VIRTUAL TABLE IF NOT EXISTS decision_fts USING fts5(title,category,scenario,outcome,reasoning,
            content='decision_search',content_rowid='id');
        CREATE TRIGGER IF NOT EXISTS decision_search_ai AFTER INSERT ON decision_search BEGIN
            INSERT INTO decision_fts(rowid,title,category,scenario,outcome,reasoning)
                VALUES(new.id,new.title,new.category,new.scenario,new.outcome,new.reasoning);
        END;
        CREATE TRIGGER IF NOT EXISTS decision_search_ad AFTER DELETE ON decision_search BEGIN
            INSERT INTO decision_fts(decision_fts,rowid,title,category,scenario,outcome,reasoning)
                VALUES('delete',old.id,old.title,old.category,old.scenario,old.outcome,old.reasoning);
        END;
        CREATE TRIGGER IF NOT EXISTS decision_search_au AFTER UPDATE ON decision_search BEGIN
            INSERT INTO decision_fts(decision_fts,rowid,title,category,scenario,outcome,reasoning)
                VALUES('delete',old.id,old.title,old.category,old.scenario,old.outcome,old.reasoning);
            INSERT INTO decision_fts(rowid,title,category,scenario,outcome,reasoning)
                VALUES(new.id,new.title,new.category,new.scenario,new.outcome,new.reasoning);
        END;
        CREATE TRIGGER IF NOT EXISTS decision_versions_search_ai AFTER INSERT ON decision_versions BEGIN
            INSERT INTO decision_search(project_id,decision_id,title,category,scenario,outcome,reasoning)
                VALUES(new.project_id,new.decision_id,new.title,new.category,new.scenario,new.outcome,new.reasoning)
            ON CONFLICT(project_id,decision_id) DO UPDATE SET
                title=excluded.title,category=excluded.category,scenario=excluded.scenario,outcome=excluded.outcome,reasoning=excluded.reasoning;
        END;
        INSERT INTO decision_search(project_id,decision_id,title,category,scenario,outcome,reasoning)
            SELECT v.project_id,v.decision_id,v.title,v.category,v.scenario,v.outcome,v.reasoning
            FROM decision_versions v JOIN decisions d ON d.project_id=v.project_id AND d.decision_id=v.decision_id AND d.current_version=v.version
            WHERE true ON CONFLICT(project_id,decision_id) DO NOTHING;
        INSERT INTO decision_fts(decision_fts) VALUES('rebuild');
        """;
}
