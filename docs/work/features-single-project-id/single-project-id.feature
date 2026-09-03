# language: en
@bdd @single-project-id
Feature: Single project id (air-merge repair + enforcement)
    As the ai-raccoon maintainers
    I want split project ids folded to one canonical id across every surface
    So that agents see one project where the bank once held several spellings

    Background:
        Given a bank with the jsaa split cluster and the AI-RACCOON casing split

    @air-merge-pint
    Rule: The repair folds every populated cluster to its canonical winner
        Scenario: Loser rows meet under the winner
            When the project-ids repair runs
            Then no labeled entries row is loser-keyed
            And the jsaa and ai-raccoon winners are registered
        Scenario: The split queue meets under the winner
            When the project-ids repair runs
            Then the winner's queue holds every row
            And no queue row is loser-keyed

    @air-merge-pint
    Rule: A watch scan never resurrects the loser id
        Scenario: Scan after repair keeps the winner key
            Given a watch for "job-search-ai-assistant" on path "/repo" with "notes-a.md" and "notes-b.md" ingested
            When the project-ids repair runs
            And a new file "notes-c.md" appears under the watched path
            And a tick runs
            Then no watch, file, digest or queue row is loser-keyed
            And "/repo" is watched under "jsaa"
            And the new file "notes-c.md" is ingested under "jsaa"
