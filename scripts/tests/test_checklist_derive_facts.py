"""`checklist-derive-facts.py`: the fact derivation the manual checklist runs before anything else.

The script exists because the two predecessor checklists pinned the version and the tool count by
hand and were wrong for three releases. Its whole value is that it fails loudly: a checklist that
gets a confident `0` is worse off than one carrying a stale number, because `0` looks derived.

It was three chained `grep`s until 0.127.0. The counting rule it implements — a line whose trimmed
content opens with `///`, `//` or `*` is prose, not code — is the fix for a real overcount (a doc
comment in `src/AiRaccoon/Tools/McpToolInventory.cs` naming the attribute it reflects over made 27
tools read as 28), and it is asserted here directly rather than through a fixture tree and a
subprocess. One end-to-end test covers the CLI contract other documents quote verbatim.
"""
from __future__ import annotations

import importlib.util
import subprocess
import sys
from pathlib import Path
from types import ModuleType

import pytest

ROOT = Path(__file__).resolve().parents[2]
DERIVE = ROOT / "scripts/checklist-derive-facts.py"

LAYOUT_HINT = ("checklist-derive-facts: the tree layout moved; fix this script rather than typing the "
               "fact by hand.")
SUCCESS_HINT = ("checklist-derive-facts: these are the checklist facts in "
                "docs/checklist/checks.json.")


def _test_write(path, data, *, encoding="utf-8"):
    Path(path).write_text(data, encoding=encoding)


@pytest.fixture(name="derive")
def _derive():
    spec = importlib.util.spec_from_file_location("checklist_derive_facts", DERIVE)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def _tree(tmp_path, *, tools="[McpServerTool] a;", prompts="[McpServerPrompt] c;",
          version="9.9.9", csproj=True, tools_dir=True, prompts_dir=True, version_file=None):
    """A fake AiRaccoon tree; each keyword removes exactly one thing the script requires."""
    src = tmp_path / "src/AiRaccoon"
    src.mkdir(parents=True, exist_ok=True)
    if version_file is not None:
        _test_write(tmp_path / "VERSION", version_file, encoding="utf-8")
    if csproj:
        _test_write(src / "AiRaccoon.csproj",
                    f"<Project><PropertyGroup><PackageVersion>{version}</PackageVersion>"
                    "</PropertyGroup></Project>", encoding="utf-8")
    if tools_dir:
        (src / "Tools").mkdir(exist_ok=True)
        _test_write(src / "Tools/Tools.cs", tools, encoding="utf-8")
    if prompts_dir:
        (src / "Prompts").mkdir(exist_ok=True)
        _test_write(src / "Prompts/Prompts.cs", prompts, encoding="utf-8")
    return tmp_path


class TestWhatCountsAsAnAttributeUse:
    """The parsing rule, tested on source text alone — no tree, no subprocess."""

    def test_a_doc_comment_naming_the_attribute_in_prose_does_not_count(self, derive: ModuleType) -> None:
        """The original defect. `McpToolInventory.cs` documents the attribute it reflects over,
        so the literal token appears in a `///` line that is prose, not a tool."""
        source = (
            "/// Derived by reflection over this assembly's own `[McpServerTool]` attributes.\n"
            "[McpServerTool] a;\n"
            "[McpServerTool] b;\n"
        )

        assert derive.count_attribute_uses(source, "McpServerTool") == 2

    def test_a_bare_attribute_with_no_name_argument_counts(self, derive: ModuleType) -> None:
        """Matching `[McpServerTool(Name` instead would repeat the same failure in reverse: a
        future tool written without a `Name` argument would silently vanish from the count."""
        assert derive.count_attribute_uses("[McpServerTool]\npublic string A() => \"\";\n",
                                           "McpServerTool") == 1

    def test_an_ordinary_named_attribute_counts(self, derive: ModuleType) -> None:
        source = '[McpServerTool(Name = "add_memory"), Description("x")]\n'

        assert derive.count_attribute_uses(source, "McpServerTool") == 1

    def test_a_line_comment_does_not_count(self, derive: ModuleType) -> None:
        assert derive.count_attribute_uses("// [McpServerTool] was here once\n",
                                           "McpServerTool") == 0

    @pytest.mark.parametrize("source,expected", [
        ("/* [McpServerTool] */", 0),
        ("/* prose\n[McpServerTool]\n*/", 0),
        ("// /*\n[McpServerTool]\n// */", 1),
        ('[Description("Use /*")]\n[McpServerTool]\n'
         '[Description("Close */")]\n[McpServerTool]', 2),
        ("/* [McpServerTool] */ [McpServerTool] real;", 1),
    ])
    def test_block_comment_contents_do_not_count(
        self, derive: ModuleType, source: str, expected: int
    ) -> None:
        assert derive.count_attribute_uses(source, "McpServerTool") == expected

    def test_a_block_comment_continuation_line_does_not_count(self, derive: ModuleType) -> None:
        """The `*` opener: a `/* */` block's inner lines carry no `//` of their own."""
        source = "/*\n * [McpServerTool] in a block comment\n */\n[McpServerTool] real;\n"

        assert derive.count_attribute_uses(source, "McpServerTool") == 1

    def test_an_indented_comment_does_not_count(self, derive: ModuleType) -> None:
        """C# doc comments sit at member indentation, never at column zero."""
        assert derive.count_attribute_uses("        /// [McpServerTool]\n", "McpServerTool") == 0

    def test_two_attributes_on_one_line_count_twice(self, derive: ModuleType) -> None:
        assert derive.count_attribute_uses("[McpServerTool] a; [McpServerTool] b;\n",
                                           "McpServerTool") == 2

    def test_an_attribute_after_code_on_the_same_line_counts(self, derive: ModuleType) -> None:
        """Legal C# and the deliberate choice here: only a line that *opens* as a comment is
        prose. Dropping any line that merely contains code would lose a real declaration."""
        assert derive.count_attribute_uses("public sealed class Tools { [McpServerTool] a; }\n",
                                           "McpServerTool") == 1

    def test_a_trailing_comment_after_a_real_attribute_still_counts_the_attribute(self, derive: ModuleType) -> None:
        assert derive.count_attribute_uses("[McpServerTool] a; // and one more\n",
                                           "McpServerTool") == 1

    def test_a_different_attribute_is_not_counted(self, derive: ModuleType) -> None:
        assert derive.count_attribute_uses("[McpServerPrompt] c;\n", "McpServerTool") == 0

    def test_the_type_level_attribute_class_is_not_a_tool_use(self, derive: ModuleType) -> None:
        """L9-8: `[McpServerToolType]` (the C# SDK's class-level marker) also contains the
        substring `[McpServerTool`, so a plain-substring count double-counts a class carrying
        the type attribute plus its own tool methods."""
        source = "[McpServerToolType]\npublic class Tools {\n[McpServerTool] a;\n[McpServerTool] b;\n}\n"

        assert derive.count_attribute_uses(source, "McpServerTool") == 2

    def test_the_prompt_type_level_attribute_class_is_not_a_prompt_use(self, derive: ModuleType) -> None:
        source = "[McpServerPromptType]\npublic class Prompts {\n[McpServerPrompt] c;\n}\n"

        assert derive.count_attribute_uses(source, "McpServerPrompt") == 1


class TestCountingAcrossATree:

    def test_attributes_in_several_files_sum(self, derive: ModuleType, tmp_path: Path) -> None:
        tools = tmp_path / "Tools"
        (tools / "nested").mkdir(parents=True)
        _test_write(tools / "A.cs", "[McpServerTool] a;\n[McpServerTool] b;\n", encoding="utf-8")
        _test_write(tools / "nested/B.cs", "/// [McpServerTool]\n[McpServerTool] c;\n",
                    encoding="utf-8")

        assert derive.count_in_tree(tools, "McpServerTool") == 3

    def test_a_non_cs_file_is_not_read(self, derive: ModuleType, tmp_path: Path) -> None:
        tools = tmp_path / "Tools"
        tools.mkdir()
        _test_write(tools / "A.cs", "[McpServerTool] a;\n", encoding="utf-8")
        _test_write(tools / "notes.md", "[McpServerTool] [McpServerTool]\n", encoding="utf-8")

        assert derive.count_in_tree(tools, "McpServerTool") == 1

    def test_a_tree_with_no_attribute_raises_rather_than_returning_zero(self, derive: ModuleType, tmp_path: Path) -> None:
        """The trap the whole script exists to close: a moved path summing to a confident 0."""
        tools = tmp_path / "Tools"
        tools.mkdir()
        _test_write(tools / "A.cs", "public class Nothing { }\n", encoding="utf-8")

        with pytest.raises(derive.DerivationFailed) as raised:
            derive.count_in_tree(tools, "McpServerTool")

        assert "zero [McpServerTool] found under" in str(raised.value)


class TestReadingTheVersion:

    def test_it_reads_the_literal_package_version(self, derive: ModuleType) -> None:
        text = "<Project><PropertyGroup><PackageVersion>1.20.0</PackageVersion></PropertyGroup>"

        assert derive.read_package_version(text) == "1.20.0"

    def test_an_empty_element_reads_as_no_version(self, derive: ModuleType) -> None:
        assert derive.read_package_version("<PackageVersion></PackageVersion>") == ""

    def test_an_msbuild_placeholder_reads_as_no_version(self, derive: ModuleType) -> None:
        """`$(Version)` is a reference to a property this script cannot evaluate, so reporting
        it verbatim would put an unresolved token in the checklist facts."""
        assert derive.read_package_version("<PackageVersion>$(Version)</PackageVersion>") == ""

    def test_a_csproj_without_the_element_reads_as_no_version(self, derive: ModuleType) -> None:
        assert derive.read_package_version("<Project></Project>") == ""


class TestEveryFailureIsLoudAndNonZero:
    """Each case exits non-zero and prints both stderr lines — the specific complaint and the
    standing instruction to fix the script rather than type the fact by hand."""

    def _fails(self, derive, capsys, tree, expected):
        code = derive.main([str(tree)])
        err = capsys.readouterr().err

        assert code != 0
        assert expected in err
        assert LAYOUT_HINT in err
        return err

    def test_a_missing_csproj(self, derive: ModuleType, capsys: pytest.CaptureFixture[str], tmp_path: Path) -> None:
        tree = _tree(tmp_path, csproj=False)

        self._fails(derive, capsys, tree, "no csproj at")

    def test_a_missing_tools_directory(self, derive: ModuleType, capsys: pytest.CaptureFixture[str], tmp_path: Path) -> None:
        tree = _tree(tmp_path, tools_dir=False)

        self._fails(derive, capsys, tree, "no tools directory at")

    def test_a_missing_prompts_directory(self, derive: ModuleType, capsys: pytest.CaptureFixture[str], tmp_path: Path) -> None:
        tree = _tree(tmp_path, prompts_dir=False)

        self._fails(derive, capsys, tree, "no prompts directory at")

    def test_an_empty_package_version(self, derive: ModuleType, capsys: pytest.CaptureFixture[str], tmp_path: Path) -> None:
        tree = _tree(tmp_path, version="")

        self._fails(derive, capsys, tree, "no version in")

    def test_an_unresolved_msbuild_placeholder_version(self, derive: ModuleType, capsys: pytest.CaptureFixture[str], tmp_path: Path) -> None:
        tree = _tree(tmp_path, version="$(Version)")

        self._fails(derive, capsys, tree, "no version in")

    def test_neither_a_csproj_element_nor_a_version_file(self, derive: ModuleType, capsys: pytest.CaptureFixture[str], tmp_path: Path) -> None:
        tree = _tree(tmp_path, version="")

        self._fails(derive, capsys, tree, "no version in")


    def test_zero_tools(self, derive: ModuleType, capsys: pytest.CaptureFixture[str], tmp_path: Path) -> None:
        tree = _tree(tmp_path, tools="public class Nothing { }")

        self._fails(derive, capsys, tree, "zero [McpServerTool] found under")

    def test_zero_prompts(self, derive: ModuleType, capsys: pytest.CaptureFixture[str], tmp_path: Path) -> None:
        tree = _tree(tmp_path, prompts="public class Nothing { }")

        self._fails(derive, capsys, tree, "zero [McpServerPrompt] found under")

    def test_a_failure_prints_nothing_to_stdout(self, derive: ModuleType, capsys: pytest.CaptureFixture[str], tmp_path: Path) -> None:
        """A caller pasting stdout into the checklist must get nothing, not a partial fact."""
        derive.main([str(_tree(tmp_path, tools="public class Nothing { }"))])

        assert capsys.readouterr().out == ""


class TestTheStdoutContract:
    """Three lines, in this order. checks.json reads them with sed, so the shape cannot drift silently."""

    def test_it_prints_exactly_the_three_derived_lines(self, derive: ModuleType, capsys: pytest.CaptureFixture[str], tmp_path: Path) -> None:
        tree = _tree(tmp_path, tools="[McpServerTool] a; [McpServerTool] b;",
                     prompts="[McpServerPrompt] c;", version="1.20.0")

        code = derive.main([str(tree)])
        captured = capsys.readouterr()

        assert code == 0
        assert captured.out == "version=1.20.0\nmcp-tool-count=2\nmcp-prompt-count=1\n"
        assert captured.err.strip() == SUCCESS_HINT

    def test_no_argument_derives_from_the_current_directory(self, derive: ModuleType, capsys: pytest.CaptureFixture[str], tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
        monkeypatch.chdir(_tree(tmp_path))

        assert derive.main([]) == 0
        assert "version=9.9.9" in capsys.readouterr().out


class TestTheCommandLineItself:
    """One subprocess, so the shebang, the exit code and the stream split are covered as a
    real invocation and not only as a function call."""

    def _run(self, tree):
        return subprocess.run([sys.executable, str(DERIVE), str(tree)],
                              capture_output=True, text=True, check=False)

    def test_a_real_run_prints_the_facts_and_the_copy_instruction(self, tmp_path: Path) -> None:
        tree = _tree(tmp_path, tools="[McpServerTool] a; [McpServerTool] b;")

        result = self._run(tree)

        assert result.returncode == 0, result.stderr
        assert result.stdout == "version=9.9.9\nmcp-tool-count=2\nmcp-prompt-count=1\n"
        assert result.stderr.strip() == SUCCESS_HINT

    def test_a_missing_tree_exits_non_zero_with_both_stderr_lines(self, tmp_path: Path) -> None:
        result = self._run(tmp_path / "nowhere")

        assert result.returncode == 1
        assert result.stdout == ""
        assert "checklist-derive-facts: no csproj at" in result.stderr
        assert LAYOUT_HINT in result.stderr

    def test_the_script_is_executable(self) -> None:
        """checks.json runs it through python3 and docs name the path; a lost exec bit turns that into a confusing shell error."""
        assert DERIVE.stat().st_mode & 0o111


class TestTheVersionFile:
    """ai-raccoon moved the version out of the csproj into a VERSION file at the repo root
    (7cfaefca, 'publish reads VERSION, not the csproj element'). The script kept reading the
    element, so it failed loudly on the very tree it ships for -- correct, but unusable."""

    def test_it_falls_back_to_the_version_file(self, derive: ModuleType, capsys: pytest.CaptureFixture[str], tmp_path: Path) -> None:
        tree = _tree(tmp_path, version="", version_file="1.20.1\n")

        assert derive.main([str(tree)]) == 0
        assert "version=1.20.1" in capsys.readouterr().out

    def test_it_strips_surrounding_whitespace(self, derive: ModuleType, capsys: pytest.CaptureFixture[str], tmp_path: Path) -> None:
        tree = _tree(tmp_path, version="", version_file="  1.21.0  \n\n")

        assert derive.main([str(tree)]) == 0
        assert "version=1.21.0" in capsys.readouterr().out

    def test_an_empty_version_file_is_not_a_version(self, derive: ModuleType, capsys: pytest.CaptureFixture[str], tmp_path: Path) -> None:
        tree = _tree(tmp_path, version="", version_file="   \n")

        assert derive.main([str(tree)]) == 1
        assert "no version in" in capsys.readouterr().err

    def test_the_csproj_element_still_wins_when_present(self, derive: ModuleType, capsys: pytest.CaptureFixture[str], tmp_path: Path) -> None:
        """A tree carrying both is not ambiguous: the element is the explicit, local statement."""
        tree = _tree(tmp_path, version="9.9.9", version_file="1.0.0\n")

        assert derive.main([str(tree)]) == 0
        assert "version=9.9.9" in capsys.readouterr().out


class TestSingleFactFlag:
    """`--fact NAME` prints one bare value, so checks.json needs no sed pipeline."""

    def _run(self, *args):
        return subprocess.run([sys.executable, str(DERIVE), *args],
                              capture_output=True, text=True, check=False)

    @pytest.mark.parametrize("name,value", [("version", "9.9.9"), ("mcp-tool-count", "2"),
                                            ("mcp-prompt-count", "1")])
    def test_it_prints_exactly_the_value(self, tmp_path: Path, name: str, value: str) -> None:
        tree = _tree(tmp_path, tools="[McpServerTool] a; [McpServerTool] b;")

        result = self._run(str(tree), "--fact", name)

        assert result.returncode == 0, result.stderr
        assert result.stdout == f"{value}\n"

    def test_an_unknown_fact_exits_2_with_nothing_on_stdout(self, tmp_path: Path) -> None:
        result = self._run(str(_tree(tmp_path)), "--fact", "nope")

        assert result.returncode == 2
        assert result.stdout == ""
        assert "nope" in result.stderr

    def test_a_failing_derivation_still_exits_1_with_nothing_on_stdout(self, tmp_path: Path) -> None:
        result = self._run(str(tmp_path / "nowhere"), "--fact", "version")

        assert result.returncode == 1
        assert result.stdout == ""
