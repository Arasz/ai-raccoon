"""Shared fixtures for the ai-raccoon Hermes memory-provider tests.

The plugin directory is named ``ai-raccoon`` (provider name = directory
name per the discovery contract) so it cannot be imported as a normal
Python package; these fixtures spec-load it the same way the Hermes
memory-plugin loader does (importlib from file location).
"""

from __future__ import annotations

import importlib.util
import pathlib
import sys

import pytest

PLUGIN_DIR = pathlib.Path(__file__).resolve().parent.parent / "ai-raccoon"
sys.path.insert(0, str(PLUGIN_DIR))  # absolute-import fallback inside the plugin


def _load_module(name: str, path: pathlib.Path):
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise ImportError(f"cannot load {path}")
    mod = importlib.util.module_from_spec(spec)
    sys.modules[name] = mod
    spec.loader.exec_module(mod)
    return mod


class FakeClient:
    """Records calls; returns canned results. Implements the client duck type."""

    def __init__(self):
        self.searches = []
        self.writes = []
        self.stats_calls = 0
        self.shares = []
        self.closed = False
        self.search_results = {"results": []}
        self.write_result = {"hash": "h1", "path": "p1", "context": "project", "createdAt": "t1"}
        self.stats_result = {"entries": 3, "pending": 0, "contexts": ["project"]}
        self.share_result = {"shared": True, "context": "shared"}

    def connect(self):
        pass

    def search(self, project_id, query, scope="all", limit=5, min_score=0.5, context_label=None):
        self.searches.append(dict(project_id=project_id, query=query, scope=scope,
                                  limit=limit, min_score=min_score, context_label=context_label))
        return self.search_results

    def write(self, project_id, content, workspace_id=None, agent_id=None,
              context=None, source_file=None, section=None):
        self.writes.append(dict(project_id=project_id, content=content, workspace_id=workspace_id,
                                agent_id=agent_id, context=context, source_file=source_file,
                                section=section))
        return self.write_result

    def stats(self, project_id):
        self.stats_calls += 1
        return self.stats_result

    def share(self, project_id, hash_):
        self.shares.append(dict(project_id=project_id, hash=hash_))
        return self.share_result

    def close(self):
        self.closed = True


def _make_provider(provider_module, config=None, fake=None):
    fake = fake or FakeClient()

    def factory(cfg):
        return fake

    provider = provider_module.AiRaccoonMemoryProvider(config=config or {}, client_factory=factory)
    return provider, fake


@pytest.fixture(scope="session")
def provider_module():
    return _load_module("ai_raccoon_plugin_test", PLUGIN_DIR / "__init__.py")


@pytest.fixture(scope="session")
def client_module():
    return _load_module("ai_raccoon_client_test", PLUGIN_DIR / "client.py")


@pytest.fixture(scope="session")
def status_module():
    return _load_module("ai_raccoon_status_test", PLUGIN_DIR / "status.py")


@pytest.fixture
def fake_client():
    return FakeClient()


@pytest.fixture
def make_provider(provider_module):
    def _make(config=None, fake=None):
        return _make_provider(provider_module, config, fake)
    return _make


def pytest_addoption(parser):
    parser.addoption("--run-slow", action="store_true", default=False,
                     help="run slow tests (spawn a real ai-raccoon server)")


def pytest_configure(config):
    config.addinivalue_line("markers", "slow: spawns a real ai-raccoon server (--run-slow)")


def pytest_collection_modifyitems(config, items):
    if config.getoption("--run-slow"):
        return
    skip_slow = pytest.mark.skip(reason="slow test; pass --run-slow to run")
    for item in items:
        if "slow" in item.keywords:
            item.add_marker(skip_slow)
