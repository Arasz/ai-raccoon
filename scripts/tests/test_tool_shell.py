"""Tests for src/tool_shell.py — DotnetToolSettings.xml rewrite logic.

Hermetic: zips are built in memory, patched via tmp_path only.
"""

import zipfile
from io import BytesIO
from typing import Optional

import pytest

from tool_shell import build_settings, find_settings, parse_settings, patch

RIDS = ["win-x64", "win-arm64", "osx-arm64", "linux-x64", "linux-arm64", "linux-musl-x64"]

SHELL_SETTINGS = """<?xml version="1.0" encoding="utf-8"?>
<DotNetCliTool Version="2">
  <Commands>
    <Command Name="ai-raccoon" />
  </Commands>
  <RuntimeIdentifierPackages>
    <RuntimeIdentifierPackage RuntimeIdentifier="win-x64" Id="ai-raccoon.win-x64" />
  </RuntimeIdentifierPackages>
</DotNetCliTool>
"""

SETTINGS_WITH_ENTRY = """<?xml version="1.0" encoding="utf-8"?>
<DotNetCliTool Version="2">
  <Commands>
    <Command Name="ai-raccoon" EntryPoint="AiRaccoon" Runner="dotnet" />
  </Commands>
  <RuntimeIdentifierPackages>
    <RuntimeIdentifierPackage RuntimeIdentifier="osx-arm64" Id="ai-raccoon.osx-arm64" />
  </RuntimeIdentifierPackages>
</DotNetCliTool>
"""

CONTENT_TYPES = """<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="xml" ContentType="application/xml" />
</Types>
"""

PAYLOAD_NAME = "tools/net10.0/any/ai-raccoon"
PAYLOAD = b"#!/bin/sh\nexec dotnet $0.dll \"$@\"\n"


def make_pkg(settings: Optional[str] = SHELL_SETTINGS, with_content_types=True, with_payload=True) -> bytes:
    buf = BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as zf:
        if settings is not None:
            zf.writestr("DotnetToolSettings.xml", settings)
        if with_content_types:
            zf.writestr("[Content_Types].xml", CONTENT_TYPES)
        if with_payload:
            zf.writestr(PAYLOAD_NAME, PAYLOAD)
    return buf.getvalue()


def write_pkg(tmp_path, data: bytes):
    pkg = tmp_path / "shell.nupkg"
    pkg.write_bytes(data)
    return pkg


def read_settings(pkg_path) -> str:
    with zipfile.ZipFile(pkg_path) as zf:
        return zf.read("DotnetToolSettings.xml").decode("utf-8")


# --- find_settings -----------------------------------------------------------

def test_find_settings_returns_the_single_settings_entry(tmp_path):
    pkg = write_pkg(tmp_path, make_pkg())
    with zipfile.ZipFile(pkg) as zf:
        assert find_settings(zf) == "DotnetToolSettings.xml"


def test_find_settings_rejects_missing_settings_file(tmp_path):
    pkg = write_pkg(tmp_path, make_pkg(settings=None, with_content_types=False, with_payload=False))
    with zipfile.ZipFile(pkg) as zf:
        with pytest.raises(SystemExit) as exc:
            find_settings(zf)
    assert exc.value.code == "expected exactly one DotnetToolSettings.xml, found []"


def test_find_settings_rejects_multiple_settings_files(tmp_path):
    buf = BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        zf.writestr("DotnetToolSettings.xml", SHELL_SETTINGS)
        zf.writestr("lib/net10.0/DotnetToolSettings.xml", SHELL_SETTINGS)
    pkg = write_pkg(tmp_path, buf.getvalue())
    with zipfile.ZipFile(pkg) as zf:
        with pytest.raises(SystemExit) as exc:
            find_settings(zf)
    assert "expected exactly one DotnetToolSettings.xml" in str(exc.value.code)


# --- parse_settings ----------------------------------------------------------

def test_parse_settings_on_packed_shell_shape():
    assert parse_settings(SHELL_SETTINGS) == ("ai-raccoon", "ai-raccoon", None, None)


def test_parse_settings_preserves_entry_point_and_runner():
    assert parse_settings(SETTINGS_WITH_ENTRY) == ("ai-raccoon", "ai-raccoon", "AiRaccoon", "dotnet")


def test_parse_settings_rejects_zero_rid_entries():
    settings = SHELL_SETTINGS.replace(
        '    <RuntimeIdentifierPackage RuntimeIdentifier="win-x64" Id="ai-raccoon.win-x64" />\n', ""
    )
    with pytest.raises(SystemExit) as exc:
        parse_settings(settings)
    # The prefix check fires first, so the zero-rid shape reports as missing
    # RuntimeIdentifierPackage — same message as the original script.
    assert exc.value.code == "unexpected shell shape: Command or RuntimeIdentifierPackage not found"


def test_parse_settings_rejects_two_rid_entries():
    settings = SHELL_SETTINGS.replace(
        "</RuntimeIdentifierPackages>",
        '    <RuntimeIdentifierPackage RuntimeIdentifier="win-arm64" Id="ai-raccoon.win-arm64" />\n'
        "  </RuntimeIdentifierPackages>",
    )
    with pytest.raises(SystemExit) as exc:
        parse_settings(settings)
    assert "expected exactly one RuntimeIdentifierPackage" in str(exc.value.code)


def test_parse_settings_rejects_rid_mismatched_package_id():
    settings = SHELL_SETTINGS.replace('Id="ai-raccoon.win-x64"', 'Id="ai-raccoon.other"')
    with pytest.raises(SystemExit) as exc:
        parse_settings(settings)
    assert "unexpected package id shape" in str(exc.value.code)


# --- build_settings ----------------------------------------------------------

def test_build_parse_round_trip_for_every_rid():
    for rid in RIDS:
        built = build_settings("ai-raccoon", "ai-raccoon", [rid])
        assert parse_settings(built) == ("ai-raccoon", "ai-raccoon", None, None)


def test_build_parse_round_trip_preserves_entry_point_and_runner():
    built = build_settings("ai-raccoon", "ai-raccoon", RIDS, entry_point="AiRaccoon", runner="dotnet")
    for rid in RIDS:
        assert parse_settings(build_settings("ai-raccoon", "ai-raccoon", [rid],
                                             entry_point="AiRaccoon", runner="dotnet")) \
            == ("ai-raccoon", "ai-raccoon", "AiRaccoon", "dotnet")
    assert 'EntryPoint="AiRaccoon"' in built
    assert 'Runner="dotnet"' in built


def test_build_settings_lists_every_rid_once():
    built = build_settings("ai-raccoon", "ai-raccoon", RIDS)
    for rid in RIDS:
        assert built.count(f'RuntimeIdentifier="{rid}"') == 1
        assert f'Id="ai-raccoon.{rid}"' in built


# --- patch (end-to-end zip rewrite) -----------------------------------------

def test_patch_rewrites_settings_with_all_rids_keeping_zip_intact(tmp_path):
    pkg = write_pkg(tmp_path, make_pkg(settings=SETTINGS_WITH_ENTRY))
    original_entries = set()
    with zipfile.ZipFile(pkg) as zf:
        original_entries = set(zf.namelist())
        content_types_before = zf.read("[Content_Types].xml")

    command, prefix = patch(pkg, RIDS)

    assert command == "ai-raccoon"
    assert prefix == "ai-raccoon"
    with zipfile.ZipFile(pkg) as zf:
        assert set(zf.namelist()) == original_entries
        assert zf.read("[Content_Types].xml") == content_types_before
        content = zf.read("DotnetToolSettings.xml").decode("utf-8")
    for rid in RIDS:
        assert content.count(f'RuntimeIdentifier="{rid}"') == 1
        assert f'Id="ai-raccoon.{rid}"' in content
    assert 'EntryPoint="AiRaccoon"' in content
    assert 'Runner="dotnet"' in content
    assert read_settings(pkg) == build_settings("ai-raccoon", "ai-raccoon", RIDS,
                                                entry_point="AiRaccoon", runner="dotnet")


def test_patch_rejects_duplicate_rids(tmp_path):
    pkg = write_pkg(tmp_path, make_pkg())
    with pytest.raises(SystemExit) as exc:
        patch(pkg, ["win-x64", "win-x64", "osx-arm64", "linux-x64", "linux-arm64", "linux-musl-x64"])
    assert exc.value.code == "duplicate rids: ['win-x64', 'win-x64', 'osx-arm64', 'linux-x64', 'linux-arm64', 'linux-musl-x64']"


def test_patch_rejects_wrong_rid_count(tmp_path):
    pkg = write_pkg(tmp_path, make_pkg())
    with pytest.raises(SystemExit) as exc:
        patch(pkg, RIDS[:5])
    assert exc.value.code == "expected 6 rids (publish.yml matrix), got 5: ['win-x64', 'win-arm64', 'osx-arm64', 'linux-x64', 'linux-arm64']"


def test_patch_rejects_package_without_settings_file(tmp_path):
    pkg = write_pkg(tmp_path, make_pkg(settings=None, with_content_types=False, with_payload=False))
    with pytest.raises(SystemExit):
        patch(pkg, RIDS)
