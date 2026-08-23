# Error recovery

Structured JSON error → classify → fix → re-run.

| Error                                                                                    | Fix                                                                               |
|------------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------|
| `config.json` invalid / `validationErrors` present                                       | Read the errors, patch `config.json`, re-run                                      |
| `manifest.json` missing or corrupt                                                       | Re-run `welcome-ai-badger` steps 4-5 (validate + scaffold)                        |
| `index.json` missing or stale                                                            | `python3 "$AI_BADGER/tooling/index_build.py"`                                     |
| `frameworkVersion` mismatch between config and framework                                 | Update `frameworkVersion` in config.json to match `cat "$AI_BADGER/VERSION"`      |
| Scaffold script raised an exception (file-permission, encoding)                          | Fix the file/permission issue, retry once                                         |
| Python dependency missing (`jsonschema`)                                                 | `python3 -m pip install -r "$AI_BADGER/engine/requirements.txt"`                  |
| Scaffold and config disagree with `reScaffolded: false` and no drift signal explains why | Re-run with `--force` — re-scaffolds unconditionally and reports `"forced": true` |
