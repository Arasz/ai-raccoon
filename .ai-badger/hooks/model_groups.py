#!/usr/bin/env python3
"""Level (low|medium|high) to model-id registry leaf.

Pure and stdlib-only: no jsonschema, no network, no wall-clock, so hooks and the
validator can both use it. `validate_registry` is the single writer of the machine
invariants (preferred-first, lexicographic price order with the demoted-tail exemption,
per-group id uniqueness, tail position, weights-identity evidence) — the half of the
contract `schemas/model-groups.schema.json` cannot express. `tooling/validate.py` calls
it; it duplicates the rules nowhere.

Resolution precedence: explicit model > level > nothing (inherit, None). The tail is
display-only: resolving never falls past index 0 and never follows `aliases`.
"""
from __future__ import annotations

import datetime
import json
import re
import warnings
from pathlib import Path
from typing import Any, Dict, List, Optional

VALID_LEVELS = ("low", "medium", "high")

ID_RE = re.compile(r"^openrouter/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
DATE_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")
VERSION_RE = re.compile(r"^\d+\.\d+\.\d+$")

TOP_REQUIRED = ("frameworkVersion", "registryVersion", "measuredAt", "groups")
MEMBER_REQUIRED = ("id", "preferred", "pricing", "evidence")
MEMBER_OPTIONAL = ("aliases", "weightsId", "status", "revisionWatch", "measuredAt")
STATUSES = ("active", "demoted", "deprecated")

__all__ = ["VALID_LEVELS", "RegistryInvalid", "UnknownLevel", "validate_registry",
           "load_groups", "preferred", "resolve"]


class RegistryInvalid(ValueError):
    """A registry document failed validation. `.errors` lists every rule broken, `.source`
    names the file it came from. Subclasses ValueError so a bare `except ValueError`
    still catches a bad registry loudly."""

    def __init__(self, errors: List[str], *, source: Any):
        self.errors = list(errors)
        self.source = str(source)
        super().__init__(f"{self.source}: invalid model-groups registry: "
                         + "; ".join(self.errors))


class UnknownLevel(ValueError):
    """A level outside low|medium|high. `.invalid_level` echoes what was passed,
    `.valid_levels` is the closed set — the fail-loud contract."""

    def __init__(self, level: Any, *, valid_levels: Any = VALID_LEVELS):
        self.invalid_level = level
        self.valid_levels = list(valid_levels)
        super().__init__(f'Unknown level "{level}". Valid levels are '
                         + ", ".join(self.valid_levels) + ".")


def _is_num(value: Any) -> bool:
    """A real number for pricing: int or float, never bool, never a string."""
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def _is_date(value: Any) -> bool:
    """Date granularity YYYY-MM-DD: shape plus a real calendar day."""
    if not isinstance(value, str) or not DATE_RE.match(value):
        return False
    try:
        datetime.date.fromisoformat(value)
    except ValueError:
        return False
    return True


def validate_registry(doc: Any, *, source: Any = "model-groups.json") -> List[str]:
    """Check `doc` against the machine invariants; return error strings, empty == valid.

    Pure: no I/O, no clock. `load_groups` raises on a non-empty return; `tooling/validate.py`
    reports it. Order of errors follows document order so two runs name the same pair first.
    """
    errors: List[str] = []
    if not isinstance(doc, dict):
        return [f"top-level registry must be an object, got {type(doc).__name__}"]
    for key in TOP_REQUIRED:
        if key not in doc:
            errors.append(f"missing required key {key!r}")
    for key in sorted(doc):
        if key not in TOP_REQUIRED and key not in ("$schema", "source"):
            errors.append(f"unexpected top-level key {key!r}")
    if not isinstance(doc.get("frameworkVersion"), str) or \
            not VERSION_RE.match(doc.get("frameworkVersion") or ""):
        errors.append("frameworkVersion must be a semver string (N.N.N)")
    version = doc.get("registryVersion")
    if not isinstance(version, int) or isinstance(version, bool) or version < 1:
        errors.append("registryVersion must be an integer >= 1")
    if not _is_date(doc.get("measuredAt")):
        errors.append("measuredAt must be a YYYY-MM-DD date")
    groups = doc.get("groups")
    if not isinstance(groups, dict):
        errors.append("groups must be an object with exactly low, medium, high")
        return errors
    for level in VALID_LEVELS:
        if level not in groups:
            errors.append(f'groups is missing "{level}"')
    for key in sorted(groups):
        if key not in VALID_LEVELS:
            errors.append(f'unexpected group "{key}": valid levels are low, medium, high')
    for level in VALID_LEVELS:
        if level in groups:
            errors.extend(_validate_group(level, groups[level]))
    errors.extend(_validate_weights_identity(groups))
    return errors


def _validate_group(level: str, members: Any) -> List[str]:
    """One group's invariants: shape, preferred-first, per-group uniqueness, price order."""
    errors: List[str] = []
    label = f"groups.{level}"
    if not isinstance(members, list) or not members:
        return [f"{label} must be a non-empty array"]
    for index, member in enumerate(members):
        errors.extend(_validate_member(label, index, member))
    flags = [m.get("preferred") is True for m in members if isinstance(m, dict)]
    if sum(flags) != 1:
        errors.append(f"{label} must have exactly one preferred member, "
                      f"found {sum(flags)}")
    elif flags[0] is not True:
        first = members[0].get("id", "?") if isinstance(members[0], dict) else "?"
        errors.append(f"{label} must list its preferred member first, "
                      f"but [0] is {first!r}")
    seen: Dict[str, int] = {}
    for index, member in enumerate(members):
        if not isinstance(member, dict) or not isinstance(member.get("id"), str):
            continue
        if member["id"] in seen:
            errors.append(f'{label}[{index}]: duplicate id {member["id"]!r} '
                          f"(first at [{seen[member['id']]}])")
        else:
            seen[member["id"]] = index
    errors.extend(_validate_order(label, members))
    return errors


def _validate_member(label: str, index: int, member: Any) -> List[str]:
    """One member's field shapes. Price order is `_validate_order`'s job, not this one's."""
    errors: List[str] = []
    where = f"{label}[{index}]"
    if not isinstance(member, dict):
        return [f"{where} must be an object"]
    for key in MEMBER_REQUIRED:
        if key not in member:
            errors.append(f"{where} is missing required key {key!r}")
    for key in sorted(member):
        if key not in MEMBER_REQUIRED and key not in MEMBER_OPTIONAL:
            errors.append(f"{where} has unexpected key {key!r}")
    ident = member.get("id")
    if "id" in member and (not isinstance(ident, str) or not ID_RE.match(ident)):
        errors.append(f"{where} has id {ident!r}: must match ^openrouter/<vendor>/<name>$")
    if "preferred" in member and not isinstance(member["preferred"], bool):
        errors.append(f"{where} preferred must be a boolean")
    if "pricing" in member:
        errors.extend(_validate_pricing(where, member["pricing"]))
    evidence = member.get("evidence")
    if "evidence" in member and (not isinstance(evidence, str) or not evidence.strip()):
        errors.append(f"{where} evidence must be a non-empty string")
    if "aliases" in member:
        aliases = member["aliases"]
        if not isinstance(aliases, list) or not aliases or \
                any(not isinstance(a, str) or not a for a in aliases):
            errors.append(f"{where} aliases must be a non-empty array of non-empty strings")
    for key in ("weightsId",):
        if key in member and (not isinstance(member[key], str) or not member[key]):
            errors.append(f"{where} {key} must be a non-empty string")
    if "status" in member and member["status"] not in STATUSES:
        errors.append(f"{where} status must be one of {', '.join(STATUSES)}")
    if "revisionWatch" in member and not isinstance(member["revisionWatch"], bool):
        errors.append(f"{where} revisionWatch must be a boolean")
    if "measuredAt" in member and not _is_date(member["measuredAt"]):
        errors.append(f"{where} measuredAt must be a YYYY-MM-DD date")
    return errors


def _validate_pricing(where: str, pricing: Any) -> List[str]:
    """A member's pricing block: two positive numbers, USD only."""
    if pricing is None or not isinstance(pricing, dict):
        return [f"{where} pricing must be an object"]
    errors: List[str] = []
    for key in ("inputPerM", "outputPerM"):
        value = pricing.get(key)
        if not _is_num(value) or value <= 0:
            errors.append(f"{where} pricing.{key} must be a number > 0")
    for key in sorted(pricing):
        if key not in ("inputPerM", "outputPerM", "currency"):
            errors.append(f"{where} pricing has unexpected key {key!r}")
    if "currency" in pricing and pricing["currency"] != "USD":
        errors.append(f"{where} pricing.currency must be \"USD\"")
    return errors


def _price_key(member: Dict[str, Any]) -> Optional[tuple]:
    """Lexicographic (input, output) order key, or None when the block is malformed
    (shape errors already reported; order cannot be judged)."""
    pricing = member.get("pricing")
    if not isinstance(pricing, dict):
        return None
    price_in, price_out = pricing.get("inputPerM"), pricing.get("outputPerM")
    if not _is_num(price_in) or not _is_num(price_out):
        return None
    return (price_in, price_out)


def _validate_order(label: str, members: List[Any]) -> List[str]:
    """Price order over the active pins plus the demoted-tail exemption.

    Non-demoted entries are non-decreasing in (input, output). Demoted entries are skipped
    for price order but must form the tail — every demoted member after every active one —
    and each must carry revisionWatch, so a demotion stays visible until revisited.
    """
    errors: List[str] = []
    last_key: Optional[tuple] = None
    seen_demoted = False
    for index, member in enumerate(members):
        if not isinstance(member, dict):
            continue
        demoted = member.get("status") == "demoted"
        if demoted:
            seen_demoted = True
            if member.get("revisionWatch") is not True:
                errors.append(f"{label}[{index}] ({member.get('id', '?')}): a demoted tail "
                              "member must carry revisionWatch: true")
            continue
        if seen_demoted:
            errors.append(f"{label}[{index}] ({member.get('id', '?')}): an active member "
                          "must not follow a demoted one — demoted members are tail-only")
        key = _price_key(member)
        if key is None:
            continue
        if last_key is not None and key < last_key:
            errors.append(f"{label}[{index}] ({member.get('id', '?')}): price order violated "
                          f"— {key} sorts before {last_key} (key is (inputPerM, outputPerM))")
        else:
            last_key = key
    return errors


def _validate_weights_identity(groups: Dict[str, Any]) -> List[str]:
    """Entries sharing a weightsId must state the relationship in evidence.

    The checkable encoding of the contract: the shared weightsId string appears in each
    twin's evidence, so a price delta between twins updates the note instead of silently
    splitting the identity. Runs across groups — twins live in medium and high.
    """
    by_weights: Dict[str, List[str]] = {}
    for level in VALID_LEVELS:
        members = groups.get(level)
        if not isinstance(members, list):
            continue
        for index, member in enumerate(members):
            if not isinstance(member, dict):
                continue
            weights = member.get("weightsId")
            if isinstance(weights, str) and weights:
                by_weights.setdefault(weights, []).append(f"groups.{level}[{index}]")
    errors: List[str] = []
    for weights, locations in sorted(by_weights.items()):
        if len(locations) < 2:
            continue
        for level in VALID_LEVELS:
            members = groups.get(level)
            if not isinstance(members, list):
                continue
            for index, member in enumerate(members):
                if not isinstance(member, dict) or member.get("weightsId") != weights:
                    continue
                evidence = member.get("evidence")
                if not isinstance(evidence, str) or weights not in evidence:
                    errors.append(f"groups.{level}[{index}] ({member.get('id', '?')}): shares "
                                  f'weightsId "{weights}" but its evidence never names it')
    return errors


def _default_path() -> Path:
    """The canonical seed, located from this file: features/common/data/model-groups.json."""
    return Path(__file__).resolve().parents[3] / "data" / "model-groups.json"


def load_groups(path: Any = None) -> Dict[str, List[Dict[str, Any]]]:
    """Load and validate a registry file; return its groups mapping.

    `path` defaults to the canonical seed beside this module. Missing, unreadable,
    unparseable, or invariant-violating files raise RegistryInvalid — never a default.
    """
    target = Path(path) if path is not None else _default_path()
    try:
        raw = target.read_text(encoding="utf-8")
    except OSError as exc:
        raise RegistryInvalid([f"cannot read registry: {exc.strerror or exc}"],
                              source=target) from exc
    try:
        doc = json.loads(raw)
    except ValueError as exc:
        raise RegistryInvalid([f"invalid JSON: {exc}"], source=target) from exc
    errors = validate_registry(doc, source=target)
    if errors:
        raise RegistryInvalid(errors, source=target)
    return doc["groups"]


def _emit_id(member: Dict[str, Any], *, source: Any) -> str:
    """The one field the resolver returns, re-validated before emit."""
    ident = member.get("id")
    if not isinstance(ident, str) or not ID_RE.match(ident):
        raise RegistryInvalid([f"refusing to emit id {ident!r}: must match "
                                "^openrouter/<vendor>/<name>$"], source=source)
    return ident


def preferred(group: Any, groups: Any = None) -> str:
    """The preferred (index-0) id of `group`. Unknown groups raise UnknownLevel."""
    name = group.strip() if isinstance(group, str) else group
    if groups is None:
        groups = load_groups()
    if not isinstance(groups, dict) or name not in VALID_LEVELS or name not in groups:
        raise UnknownLevel(group)
    members = groups[name]
    if not isinstance(members, list) or not members:
        raise RegistryInvalid([f"groups.{name} must be a non-empty array"],
                              source="model-groups.json")
    first = members[0]
    if not isinstance(first, dict):
        raise RegistryInvalid([f"groups.{name}[0] must be an object"],
                              source="model-groups.json")
    return _emit_id(first, source="model-groups.json")


def resolve(level: Any = None, explicit_model: Any = None, groups: Any = None
            ) -> Optional[str]:
    """Resolve a dispatch to a model id: explicit model wins verbatim, else the level's
    preferred pin, else None (no pin — inherit the session model).

    A stale level alongside an explicit model warns instead of failing: the level decides
    nothing there. A deciding stale level raises UnknownLevel. Resolution is
    case-sensitive; surrounding whitespace is stripped; blank is absent.
    """
    norm = level.strip() if isinstance(level, str) else level
    if explicit_model is not None and (not isinstance(explicit_model, str)
                                       or explicit_model.strip() != ""):
        if norm is not None and norm != "" and norm not in VALID_LEVELS:
            warnings.warn(UnknownLevel(norm).args[0] + " The explicit model wins; "
                          "the level was ignored.", UserWarning, stacklevel=2)
        return explicit_model
    if norm is None or (isinstance(norm, str) and norm == ""):
        return None
    if norm not in VALID_LEVELS:
        raise UnknownLevel(norm)
    return preferred(norm, groups)
