"""package_verify: csproj version parse, RID detection, nupkg entry checks (hermetic; no dotnet)."""

import hashlib
import io
import zipfile

import pytest

import bundle
import package_verify

CSPROJ_WITH_VERSION = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>ai-raccoon</PackageId>
    <PackageVersion>1.0.10</PackageVersion>
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


def test_parse_csproj_version_found(tmp_path):
    csproj = tmp_path / "AiRaccoon.csproj"
    csproj.write_text(CSPROJ_WITH_VERSION)
    assert package_verify.parse_csproj_version(csproj) == "1.0.10"


def test_parse_csproj_version_missing(tmp_path):
    csproj = tmp_path / "AiRaccoon.csproj"
    csproj.write_text('<Project Sdk="Microsoft.NET.Sdk"></Project>')
    assert package_verify.parse_csproj_version(csproj) is None


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


def test_required_entries_derive_from_bundle_pins():
    assert package_verify.REQUIRED_ENTRIES == (
        "Models/" + bundle.MODEL_NAME,
        "Models/" + bundle.VOCAB_NAME,
    )


def test_missing_entries_none_when_all_present():
    z = _zip_bytes([("Models/model_qint8_arm64.onnx", b"model"), ("Models/vocab.txt", b"vocab")])
    assert package_verify.missing_entries(z) == []


def test_missing_entries_detects_absent_model():
    z = _zip_bytes([("Models/vocab.txt", b"vocab")])
    assert package_verify.missing_entries(z) == ["Models/" + bundle.MODEL_NAME]


def test_missing_entries_requires_exact_names():
    z = _zip_bytes([("Models/model_qint8_arm64.onnx.bak", b"model"), ("Models/vocab.txt", b"vocab")])
    assert package_verify.missing_entries(z) == ["Models/" + bundle.MODEL_NAME]


def test_entry_sha256_matches_hashlib_reference():
    data = b"known model bytes for the sha256 test"
    z = _zip_bytes([("Models/model_qint8_arm64.onnx", data)])
    assert package_verify.entry_sha256(z, "Models/" + bundle.MODEL_NAME) == hashlib.sha256(data).hexdigest()


def test_verify_nupkg_reports_missing_entry(tmp_path, capsys):
    nupkg = tmp_path / "ai-raccoon.1.0.10.nupkg"
    _write_zip(nupkg, [("Models/vocab.txt", b"vocab")])
    assert package_verify.verify_nupkg(nupkg, "1.0.10") == 1
    assert "FAIL: Models/model_qint8_arm64.onnx missing from %s" % nupkg in capsys.readouterr().err


def test_verify_nupkg_reports_sha_mismatch_against_bundle_pin(tmp_path, capsys):
    model_data = b"not the real model"
    nupkg = tmp_path / "ai-raccoon.1.0.10.nupkg"
    _write_zip(nupkg, [("Models/model_qint8_arm64.onnx", model_data), ("Models/vocab.txt", b"vocab")])
    assert package_verify.verify_nupkg(nupkg, "1.0.10") == 1
    captured = capsys.readouterr()
    assert "present in package: Models/model_qint8_arm64.onnx" in captured.out
    assert "present in package: Models/vocab.txt" in captured.out
    expected = "FAIL: model sha256 mismatch: expected %s, got %s" % (
        bundle.MODEL_SHA256,
        hashlib.sha256(model_data).hexdigest(),
    )
    assert expected in captured.err
