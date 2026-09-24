"""package_verify: VERSION lookup, RID detection, nupkg entry checks (hermetic; no dotnet)."""

import hashlib
import io
import zipfile

import pytest

import bundle
import package_verify

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>ai-raccoon</PackageId>
  </PropertyGroup>
</Project>
"""


def _write_zip(target, entries):
    with zipfile.ZipFile(target, "w") as zf:
        for name, data in entries:
            zf.writestr(name, data)


def _zip_bytes(entries):
    buf = io.BytesIO()
    _write_zip(buf, entries)
    buf.seek(0)
    return buf


def test_read_version_walks_up_to_the_repo_root(tmp_path):
    """The csproj lives at src/AiRaccoon/; VERSION lives at the repo root above it."""
    proj_dir = tmp_path / "src" / "AiRaccoon"
    proj_dir.mkdir(parents=True)
    csproj = proj_dir / "AiRaccoon.csproj"
    csproj.write_text(CSPROJ)
    (tmp_path / "VERSION").write_text("1.0.10\n")
    assert package_verify.read_version(csproj) == "1.0.10"


def test_read_version_missing_file_returns_none(tmp_path):
    csproj = tmp_path / "AiRaccoon.csproj"
    csproj.write_text(CSPROJ)
    assert package_verify.read_version(csproj) is None


@pytest.mark.parametrize(
    "system,machine,expected",
    [
        ("Darwin", "arm64", "osx-arm64"),
        ("Darwin", "x86_64", "osx-x64"),
        ("Linux", "x86_64", "linux-x64"),
        ("Linux", "aarch64", "linux-arm64"),
    ],
)
def test_rid_from_uname_mapping(system, machine, expected):
    assert package_verify.rid_from_uname(system, machine) == expected


def test_rid_from_uname_unknown_machine():
    assert package_verify.rid_from_uname("Linux", "armv7l") is None


def test_detect_rid_prefers_dotnet(monkeypatch):
    monkeypatch.setattr(package_verify, "_dotnet_rid", lambda: "osx-arm64")
    assert package_verify.detect_rid() == "osx-arm64"


def test_detect_rid_falls_back_to_uname(monkeypatch):
    monkeypatch.setattr(package_verify, "_dotnet_rid", lambda: None)
    monkeypatch.setattr(package_verify, "rid_from_uname", lambda system, machine: "linux-x64")
    assert package_verify.detect_rid() == "linux-x64"


def test_detect_rid_failure_exits_1(monkeypatch, capsys):
    monkeypatch.setattr(package_verify, "_dotnet_rid", lambda: None)
    monkeypatch.setattr(package_verify, "rid_from_uname", lambda system, machine: None)
    with pytest.raises(SystemExit) as exc:
        package_verify.detect_rid()
    assert exc.value.code == 1
    assert "cannot determine host RID (dotnet --info unavailable)" in capsys.readouterr().err


_MODEL_ENTRY = "Models/%s/model_fp16.onnx" % bundle.BUNDLED_DIR


def _all_required(overrides=None):
    data = {entry: b"x" for entry in package_verify.REQUIRED_ENTRIES}
    data.update(overrides or {})
    return list(data.items())


def test_required_entries_derive_from_bundle_pins():
    model_dir = "Models/%s/" % bundle.BUNDLED_DIR
    assert package_verify.REQUIRED_ENTRIES == (
        tuple(model_dir + name for name, _url, _sha in bundle.BUNDLED_FILES)
        + (model_dir + bundle.BUNDLED_MANIFEST[0], "Models/" + bundle.VOCAB_NAME)
    )


def test_missing_entries_none_when_all_present():
    assert package_verify.missing_entries(_zip_bytes(_all_required())) == []


def test_missing_entries_detects_absent_model():
    entries = [(name, data) for name, data in _all_required() if name != _MODEL_ENTRY]
    assert package_verify.missing_entries(_zip_bytes(entries)) == [_MODEL_ENTRY]


def test_missing_entries_requires_exact_names():
    entries = [(name + ".bak" if name == _MODEL_ENTRY else name, data) for name, data in _all_required()]
    assert package_verify.missing_entries(_zip_bytes(entries)) == [_MODEL_ENTRY]


def test_entry_sha256_matches_hashlib_reference():
    data = b"known model bytes for the sha256 test"
    z = _zip_bytes([(_MODEL_ENTRY, data)])
    assert package_verify.entry_sha256(z, _MODEL_ENTRY) == hashlib.sha256(data).hexdigest()


def test_verify_nupkg_reports_missing_entry(tmp_path, capsys):
    nupkg = tmp_path / "ai-raccoon.1.0.10.nupkg"
    _write_zip(nupkg, [("Models/vocab.txt", b"vocab")])
    assert package_verify.verify_nupkg(nupkg, "1.0.10") == 1
    assert "FAIL: %s missing from %s" % (_MODEL_ENTRY, nupkg) in capsys.readouterr().err


def test_verify_nupkg_reports_sha_mismatch_against_bundle_pin(tmp_path, capsys):
    model_data = b"not the real model"
    nupkg = tmp_path / "ai-raccoon.1.0.10.nupkg"
    _write_zip(nupkg, _all_required({_MODEL_ENTRY: model_data}))
    assert package_verify.verify_nupkg(nupkg, "1.0.10") == 1
    captured = capsys.readouterr()
    assert "present in package: %s" % _MODEL_ENTRY in captured.out
    assert "present in package: Models/vocab.txt" in captured.out
    pinned = dict((name, sha) for name, _url, sha in bundle.BUNDLED_FILES)["model_fp16.onnx"]
    expected = "FAIL: %s sha256 mismatch: expected %s, got %s" % (
        _MODEL_ENTRY,
        pinned,
        hashlib.sha256(model_data).hexdigest(),
    )
    assert expected in captured.err


# --- suffix-based matching: a required entry is satisfied by ANY path ending in it (package root
# or tools/net10.0/<rid>/…), and it is a failure for it to be packed more than once anywhere. ---


def test_entries_matching_finds_every_path_ending_in_the_suffix():
    suffix = "Models/x/model.bin"
    z = _zip_bytes([(suffix, b"data"), ("tools/net10.0/osx-arm64/" + suffix, b"data"), ("Models/other.txt", b"nope")])
    assert package_verify.entries_matching(z, suffix) == [suffix, "tools/net10.0/osx-arm64/" + suffix]


def test_missing_entries_satisfied_by_a_rid_scoped_copy_alone(monkeypatch):
    monkeypatch.setattr(package_verify, "REQUIRED_ENTRIES", ("Models/x/model.bin",))
    z = _zip_bytes([("tools/net10.0/osx-arm64/Models/x/model.bin", b"data")])
    assert package_verify.missing_entries(z) == []


def test_missing_entries_still_reports_absent_suffix(monkeypatch):
    monkeypatch.setattr(package_verify, "REQUIRED_ENTRIES", ("Models/x/model.bin",))
    z = _zip_bytes([("Models/x/other.bin", b"data")])
    assert package_verify.missing_entries(z) == ["Models/x/model.bin"]


def test_duplicate_entries_empty_when_packed_exactly_once(monkeypatch):
    monkeypatch.setattr(package_verify, "REQUIRED_ENTRIES", ("Models/x/model.bin",))
    z = _zip_bytes([("tools/net10.0/osx-arm64/Models/x/model.bin", b"data")])
    assert package_verify.duplicate_entries(z) == []


def test_duplicate_entries_flags_a_suffix_packed_at_root_and_under_tools(monkeypatch):
    monkeypatch.setattr(package_verify, "REQUIRED_ENTRIES", ("Models/x/model.bin",))
    z = _zip_bytes([("Models/x/model.bin", b"data"), ("tools/net10.0/osx-arm64/Models/x/model.bin", b"data")])
    duplicates = package_verify.duplicate_entries(z)
    assert len(duplicates) == 1
    entry, matches = duplicates[0]
    assert entry == "Models/x/model.bin"
    assert sorted(matches) == ["Models/x/model.bin", "tools/net10.0/osx-arm64/Models/x/model.bin"]


def test_verify_nupkg_fails_when_the_bundled_weights_are_packed_twice(tmp_path, capsys, monkeypatch):
    monkeypatch.setattr(package_verify, "REQUIRED_ENTRIES", ("Models/x/model.bin",))
    monkeypatch.setattr(package_verify, "_BUNDLED_PINS", (("Models/x/model.bin", hashlib.sha256(b"data").hexdigest()),))
    nupkg = tmp_path / "ai-raccoon.osx-arm64.1.0.10.nupkg"
    _write_zip(nupkg, [("Models/x/model.bin", b"data"), ("tools/net10.0/osx-arm64/Models/x/model.bin", b"data")])
    assert package_verify.verify_nupkg(nupkg, "1.0.10") == 1
    err = capsys.readouterr().err
    assert "Models/x/model.bin" in err
    assert "packed 2 times" in err


def test_verify_nupkg_passes_when_the_bundled_weights_are_packed_exactly_once(tmp_path, monkeypatch):
    monkeypatch.setattr(package_verify, "REQUIRED_ENTRIES", ("Models/x/model.bin",))
    monkeypatch.setattr(package_verify, "_BUNDLED_PINS", (("Models/x/model.bin", hashlib.sha256(b"data").hexdigest()),))
    nupkg = tmp_path / "ai-raccoon.osx-arm64.1.0.10.nupkg"
    _write_zip(nupkg, [("tools/net10.0/osx-arm64/Models/x/model.bin", b"data")])
    assert package_verify.verify_nupkg(nupkg, "1.0.10") == 0


def test_verify_no_bundled_weights_passes_for_a_clean_top_level_shim(tmp_path, monkeypatch):
    monkeypatch.setattr(package_verify, "REQUIRED_ENTRIES", ("Models/x/model.bin",))
    nupkg = tmp_path / "ai-raccoon.1.0.10.nupkg"
    _write_zip(nupkg, [("tools/net10.0/any/ai-raccoon.dll", b"shim")])
    assert package_verify.verify_no_bundled_weights(nupkg) == 0


def test_verify_no_bundled_weights_fails_when_the_shim_still_carries_a_copy(tmp_path, capsys, monkeypatch):
    monkeypatch.setattr(package_verify, "REQUIRED_ENTRIES", ("Models/x/model.bin",))
    nupkg = tmp_path / "ai-raccoon.1.0.10.nupkg"
    _write_zip(nupkg, [("Models/x/model.bin", b"data")])
    assert package_verify.verify_no_bundled_weights(nupkg) == 1
    assert "unexpectedly present" in capsys.readouterr().err
