"""Unit tests for retrieval_tuning.settings — verb mapping, explicit defaults, argv building."""

import os
import socket

import pytest

from retrieval_tuning import settings as s
from retrieval_tuning.server import SafetyViolation


@pytest.fixture
def fake_settings_binary(tmp_path):
    """A fake `ai-raccoon` that records its argv lines into a log and exits 0."""
    log = tmp_path / "calls.log"
    script = tmp_path / "fake-settings.py"
    script.write_text(
        "#!/usr/bin/env python3\n"
        "import os, sys\n"
        "with open(os.environ['FAKE_BIN_LOG'], 'a') as f:\n"
        "    f.write('|'.join(sys.argv[1:]) + '\\n')\n"
        "sys.exit(int(os.environ.get('FAKE_BIN_EXIT', '0')))\n"
    )
    os.chmod(script, 0o755)
    return script, log


def read_calls(log_path):
    return [line.split("|") for line in log_path.read_text().splitlines() if line]


class TestKnobVerbs:
    def test_verb_map_matches_plan(self):
        assert s.knob_verb("rrfK") == "rrfk"
        assert s.knob_verb("ftsWeight") == "fts-weight"
        assert s.knob_verb("vectorWeight") == "vector-weight"
        assert s.knob_verb("sourceLambda") == "source-lambda"
        assert s.knob_verb("consolidationThreshold") == "consolidation"
        assert s.knob_verb("docScoreFormula") == "doc-formula"
        assert s.knob_verb("candidateWindow") == "window"
        assert s.knob_verb("structureAlpha") == "alpha"
        assert s.knob_verb("fusion") == "fusion"

    def test_unknown_knob_raises(self):
        with pytest.raises(ValueError):
            s.knob_verb("notAKnob")


class TestSettingsCommandArgv:
    def test_set_int(self):
        assert s.settings_command("rrfK", 60) == ["settings", "retrieval", "rrfk", "set", "60"]

    def test_set_float_keeps_invariant_repr(self):
        assert s.settings_command("sourceLambda", 0.05) == [
            "settings", "retrieval", "source-lambda", "set", "0.05",
        ]

    def test_set_enum(self):
        assert s.settings_command("docScoreFormula", "sum") == [
            "settings", "retrieval", "doc-formula", "set", "sum",
        ]

    def test_set_window(self):
        assert s.settings_command("candidateWindow", "max5x50") == [
            "settings", "retrieval", "window", "set", "max5x50",
        ]

    def test_set_alpha(self):
        assert s.settings_command("structureAlpha", 0.25) == [
            "settings", "retrieval", "alpha", "set", "0.25",
        ]

    def test_fusion_enable(self):
        assert s.settings_command("fusion", True) == ["settings", "retrieval", "fusion", "enable"]

    def test_fusion_disable(self):
        assert s.settings_command("fusion", False) == ["settings", "retrieval", "fusion", "disable"]

    def test_show_action(self):
        assert s.settings_command("rrfK", action="show") == ["settings", "retrieval", "rrfk", "show"]

    def test_show_all_command(self):
        assert s.show_all_command() == ["settings", "retrieval", "show-all"]

    def test_unknown_knob_raises(self):
        with pytest.raises(ValueError):
            s.settings_command("nope", 1)


class TestFullArgv:
    def test_argv_has_data_root_port_and_verb(self):
        argv = s.settings_argv(
            "/usr/bin/fake-ai-raccoon", "/tmp/scratch-root", "rrfK", 5, action="set", port=48321
        )
        assert argv == [
            "/usr/bin/fake-ai-raccoon",
            "--data-root", "/tmp/scratch-root",
            "--port", "48321",
            "settings", "retrieval", "rrfk", "set", "5",
        ]


class TestKnobDefaults:
    def test_defaults_are_the_nine_plan_values(self):
        assert s.KNOB_DEFAULTS == {
            "rrfK": 60,
            "ftsWeight": 1,
            "vectorWeight": 1,
            "sourceLambda": 0.1,
            "consolidationThreshold": 0.1,
            "docScoreFormula": "max",
            "candidateWindow": "max3x100",
            "structureAlpha": 0.5,
            "fusion": False,
        }


class TestApplySettings:
    def test_applies_knobs_via_subprocess(self, tmp_path, fake_settings_binary, monkeypatch):
        script, log = fake_settings_binary
        monkeypatch.setenv("FAKE_BIN_LOG", str(log))
        out = s.apply_settings(tmp_path, {"rrfK": 5, "fusion": True}, port=48321, binary=str(script))
        calls = read_calls(log)
        assert len(calls) == 2
        assert calls[0][:2] == ["--data-root", str(tmp_path)]
        assert calls[0][2:4] == ["--port", "48321"]
        assert calls[0][4:] == ["settings", "retrieval", "rrfk", "set", "5"]
        assert calls[1][4:] == ["settings", "retrieval", "fusion", "enable"]

    def test_failure_raises_with_stderr(self, tmp_path, fake_settings_binary, monkeypatch):
        script, log = fake_settings_binary
        monkeypatch.setenv("FAKE_BIN_LOG", str(log))
        monkeypatch.setenv("FAKE_BIN_EXIT", "2")
        with pytest.raises(s.SettingsError) as exc:
            s.apply_settings(tmp_path, {"rrfK": 5}, port=48321, binary=str(script))
        assert "exit 2" in str(exc.value)

    def test_unknown_knob_raises_before_spawn(self, tmp_path, fake_settings_binary, monkeypatch):
        script, log = fake_settings_binary
        monkeypatch.setenv("FAKE_BIN_LOG", str(log))
        with pytest.raises(ValueError):
            s.apply_settings(tmp_path, {"bogus": 1}, port=48321, binary=str(script))
        assert not log.exists()

    def test_port_7721_refused(self, tmp_path):
        with pytest.raises(SafetyViolation):
            s.apply_settings(tmp_path, {"rrfK": 5}, port=7721, binary="whatever")


class TestResetToDefaults:
    def test_writes_all_nine_defaults_explicitly(self, tmp_path, fake_settings_binary, monkeypatch):
        script, log = fake_settings_binary
        monkeypatch.setenv("FAKE_BIN_LOG", str(log))
        s.reset_to_defaults(tmp_path, port=48321, binary=str(script))
        calls = read_calls(log)
        assert len(calls) == 9
        expected = {
            ("rrfk", "60"),
            ("fts-weight", "1"),
            ("vector-weight", "1"),
            ("source-lambda", "0.1"),
            ("consolidation", "0.1"),
            ("doc-formula", "max"),
            ("window", "max3x100"),
            ("alpha", "0.5"),
            ("fusion", "disable"),
        }
        got = {(c[6], c[7] if len(c) == 8 else c[8]) for c in calls}  # (verb, value); fusion has no value slot
        assert got == expected


class TestParseShowAll:
    def test_parses_name_value_source_lines(self):
        text = (
            "rrfK: 5  (setting)\n"
            "ftsWeight: 1  (default)\n"
            "fusionNoRegressionEnabled: false  (default)\n"
        )
        parsed = s.parse_show_all(text)
        assert parsed["rrfK"] == ("5", "setting")
        assert parsed["ftsWeight"] == ("1", "default")
        assert parsed["fusionNoRegressionEnabled"] == ("false", "default")

    def test_parse_fusion_show(self):
        assert s.parse_fusion_show("enabled: True  (default: False — off serves the baseline fusion)") is True
        assert s.parse_fusion_show("enabled: False  (default: False — off serves the baseline fusion)") is False


class TestPickFreePort:
    def test_returns_bindable_port_not_7721(self):
        port = s.pick_free_port()
        assert port != 7721
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", port))
