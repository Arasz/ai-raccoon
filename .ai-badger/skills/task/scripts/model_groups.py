#!/usr/bin/env python3
"""Model map leaf: cost tier (low|medium|high|top) to model id.

Pure and stdlib-only: no jsonschema, no network, no wall-clock, so hooks and the
validator can both use it. `validate_map` is the single writer of the machine
invariants (one anchor per declared family per group, the primary anchor and the
member behind it, the family every member carries, the open-weight price rule, one
preferred member per group and its position, the price order, per-group id
uniqueness) — the half of the contract `schemas/model-groups.schema.json` cannot
express. `tooling/validate.py` calls it; it duplicates the rules nowhere.

Resolution precedence: explicit model > level > nothing (inherit, None). A level
names a cost tier; the tier's preferred member is the id it resolves to.
"""
from __future__ import annotations

import datetime
import json
import re
import warnings
from pathlib import Path
from typing import Any, Dict, List, Optional

VALID_LEVELS = ("low", "medium", "high", "top")

MODEL_ID_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]*(?:/[A-Za-z0-9~@][A-Za-z0-9._~@:+-]*)+")
DATE_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")
VERSION_RE = re.compile(r"^\d+\.\d+\.\d+$")

TOP_REQUIRED = ("frameworkVersion", "registryVersion", "measuredAt", "families", "groups")
MEMBER_REQUIRED = ("id", "family", "preferred", "pricing", "license", "score", "evidence")
GROUP_REQUIRED = ("anchors", "primary", "models")
PRICING_KEYS = ("inputPerM", "outputPerM", "currency")

#: A license naming an open-weight release: the zero-price relaxation belongs to these.
OPEN_LICENSE_MARKERS = ("open", "apache", "mit", "bsd", "gpl", "free", "public domain")

__all__ = ["VALID_LEVELS", "RegistryInvalid", "UnknownLevel", "validate_map",
           "validate_registry", "load_groups", "preferred", "resolve"]


class RegistryInvalid(ValueError):
    """A map document failed validation. `.errors` lists every rule broken, `.source`
    names the file it came from. Subclasses ValueError so a bare `except ValueError`
    still catches a bad map loudly."""

    def __init__(self, errors: List[str], *, source: Any):
        self.errors = list(errors)
        self.source = str(source)
        super().__init__(f"{self.source}: invalid model-groups map: "
                         + "; ".join(self.errors))


class UnknownLevel(ValueError):
    """A level outside low|medium|high|top. `.invalid_level` echoes what was passed,
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


def _is_open_weight(license_name: Any) -> bool:
    """True when the license names an open-weight release."""
    if not isinstance(license_name, str):
        return False
    lowered = license_name.lower()
    return any(marker in lowered for marker in OPEN_LICENSE_MARKERS)


def validate_map(doc: Any, *, source: Any = "model-groups.json") -> List[str]:
    """Check `doc` against the machine invariants; return error strings, empty == valid.

    Pure: no I/O, no clock. `load_groups` raises on a non-empty return; `tooling/validate.py`
    reports it. Order of errors follows document order so two runs name the same pair first.
    """
    errors: List[str] = []
    if not isinstance(doc, dict):
        return [f"top-level map must be an object, got {type(doc).__name__}"]
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
    errors.extend(_validate_families(doc.get("families")))
    declared = doc.get("families")
    families = [f for f in declared if isinstance(f, str) and f.strip()] \
        if isinstance(declared, list) else []
    errors.extend(_validate_source(doc.get("source")))
    groups = doc.get("groups")
    if not isinstance(groups, dict):
        errors.append("groups must be an object with exactly low, medium, high, top")
        return errors
    for level in VALID_LEVELS:
        if level not in groups:
            errors.append(f'groups is missing "{level}"')
    for key in sorted(groups):
        if key not in VALID_LEVELS:
            errors.append(f'unexpected group "{key}": valid levels are '
                          + ", ".join(VALID_LEVELS))
    for level in VALID_LEVELS:
        if level in groups:
            errors.extend(_validate_group(level, groups[level], families))
    return errors


#: The old name of the validator; the map is what the registry file now holds.
validate_registry = validate_map


def _validate_families(families: Any) -> List[str]:
    """The declared families: a non-empty list of distinct, non-empty names."""
    if not isinstance(families, list) or not families:
        return ["families must be a non-empty array of family names"]
    errors: List[str] = []
    seen = set()
    for family in families:
        if not isinstance(family, str) or not family.strip():
            errors.append("families must hold non-empty family names")
            continue
        if family in seen:
            errors.append(f"family {family!r} is declared twice")
        seen.add(family)
    return errors


def _validate_source(source: Any) -> List[str]:
    """The provenance block: where the prices and scores came from, checkably."""
    if source is None:
        return []
    if not isinstance(source, dict):
        return ["source must be an object"]
    errors: List[str] = []
    leaderboard = source.get("leaderboard")
    if not isinstance(leaderboard, dict):
        errors.append("source.leaderboard must name the pricing source")
        return errors
    if not isinstance(leaderboard.get("path"), str) or not leaderboard.get("path"):
        errors.append("source.leaderboard.path must be a non-empty string")
    rows = leaderboard.get("rows")
    if not isinstance(rows, int) or isinstance(rows, bool) or rows < 1:
        errors.append("source.leaderboard.rows must be an integer >= 1")
    checksum = leaderboard.get("sha256")
    if not isinstance(checksum, str) or not re.match(r"^[0-9a-f]{64}$", checksum):
        errors.append("source.leaderboard.sha256 must be a 64-character hex digest")
    return errors


def _validate_group(level: str, entry: Any, families: List[str]) -> List[str]:
    """One group's invariants: anchor coverage, the primary anchor and the member
    behind it, member shapes, the preferred member, per-group id uniqueness and the
    price order."""
    errors: List[str] = []
    label = f"groups.{level}"
    if not isinstance(entry, dict):
        return [f"{label} must be an object"]
    for key in GROUP_REQUIRED:
        if key not in entry:
            errors.append(f"{label} is missing required key {key!r}")
    errors.extend(_validate_anchors(label, entry.get("anchors"), families))
    models = entry.get("models")
    if not isinstance(models, list) or not models:
        errors.append(f"{label} must carry a non-empty models array")
        return errors
    for index, member in enumerate(models):
        errors.extend(_validate_member(label, index, member))
    flags = [m.get("preferred") is True for m in models if isinstance(m, dict)]
    if sum(flags) != 1:
        errors.append(f"{label} must have exactly one preferred member, "
                      f"found {sum(flags)}")
    elif flags[0] is not True:
        first = models[0].get("id", "?") if isinstance(models[0], dict) else "?"
        errors.append(f"{label} must list its preferred member first, "
                      f"but [0] is {first!r}")
    errors.extend(_validate_primary(label, entry, models))
    seen: Dict[str, int] = {}
    for index, member in enumerate(models):
        if not isinstance(member, dict) or not isinstance(member.get("id"), str):
            continue
        if member["id"] in seen:
            errors.append(f'{label}[{index}]: duplicate id {member["id"]!r} '
                          f"(first at [{seen[member['id']]}])")
        else:
            seen[member["id"]] = index
    errors.extend(_validate_order(label, models))
    return errors


def _validate_primary(label: str, entry: Dict[str, Any], models: List[Any]) -> List[str]:
    """The group's primary anchor and the member behind it.

    The primary names one anchor key, and that anchor's family is the family of the
    group's preferred member. Some member has to carry the anchor's family, or the anchor
    decides nothing and the group resolves to a model it does not stand for.
    """
    errors: List[str] = []
    anchors = entry.get("anchors")
    primary = entry.get("primary")
    if not isinstance(primary, str) or not primary.strip():
        errors.append(f"{label} must name a primary anchor")
        return errors
    if not isinstance(anchors, dict) or primary not in anchors:
        errors.append(f'{label} names the primary anchor {primary!r}, which the group '
                      "does not anchor")
        return errors
    anchor = anchors[primary]
    declared = [member.get("family") for member in models
                if isinstance(member, dict) and isinstance(member.get("family"), str)]
    if anchor not in declared:
        errors.append(f"{label}: the primary anchor {primary!r} names the family "
                      f"{anchor!r}, which no member carries")
    preferred = [member for member in models
                 if isinstance(member, dict) and member.get("preferred") is True]
    if len(preferred) == 1:
        family = preferred[0].get("family")
        if isinstance(family, str) and family != anchor:
            errors.append(f"{label}: the preferred member "
                          f"{preferred[0].get('id', '?')!r} is the family {family!r}, "
                          f"not the primary anchor {anchor!r}")
    return errors


def _validate_anchors(label: str, anchors: Any, families: List[str]) -> List[str]:
    """Every group anchors every declared family; a family may anchor several groups."""
    if not isinstance(anchors, dict) or not anchors:
        return [f"{label} must carry an anchors object"]
    errors: List[str] = []
    for family in families:
        if family not in anchors:
            errors.append(f"{label} is missing an anchor for the declared family "
                          f"{family!r}")
    for family, anchor in sorted(anchors.items()):
        if family not in families:
            errors.append(f"{label} anchors {family!r}, which the map does not declare")
        if not isinstance(anchor, str) or not anchor.strip():
            errors.append(f"{label} has an empty anchor for {family!r}")
    return errors


def _validate_member(label: str, index: int, member: Any) -> List[str]:
    """One member's field shapes and the price rule."""
    errors: List[str] = []
    where = f"{label}[{index}]"
    if not isinstance(member, dict):
        return [f"{where} must be an object"]
    for key in MEMBER_REQUIRED:
        if key not in member:
            errors.append(f"{where} is missing required key {key!r}")
    for key in sorted(member):
        if key not in MEMBER_REQUIRED:
            errors.append(f"{where} has unexpected key {key!r}")
    ident = member.get("id")
    if "id" in member and (not isinstance(ident, str) or not MODEL_ID_RE.fullmatch(ident)):
        errors.append(f"{where} has id {ident!r}: must be provider-qualified "
                      "<provider>/<model>")
    family = member.get("family")
    if "family" in member and family is not None and \
            (not isinstance(family, str) or not family.strip()):
        errors.append(f"{where} family must be a non-empty family name or null")
    if "preferred" in member and not isinstance(member["preferred"], bool):
        errors.append(f"{where} preferred must be a boolean")
    if "pricing" in member:
        errors.extend(_validate_pricing(where, member["pricing"]))
    license_name = member.get("license")
    if "license" in member and (not isinstance(license_name, str)
                                or not license_name.strip()):
        errors.append(f"{where} license must be a non-empty string")
    if "score" in member and not _is_num(member["score"]):
        errors.append(f"{where} score must be a number")
    evidence = member.get("evidence")
    if "evidence" in member and (not isinstance(evidence, str) or not evidence.strip()):
        errors.append(f"{where} evidence must be a non-empty string")
    errors.extend(_validate_price_rule(where, member))
    return errors


def _validate_pricing(where: str, pricing: Any) -> List[str]:
    """A member's pricing block: two non-negative numbers, USD only."""
    if not isinstance(pricing, dict):
        return [f"{where} pricing must be an object"]
    errors: List[str] = []
    for key in ("inputPerM", "outputPerM"):
        value = pricing.get(key)
        if not _is_num(value) or value < 0:
            errors.append(f"{where} pricing.{key} must be a number >= 0")
    for key in sorted(pricing):
        if key not in PRICING_KEYS:
            errors.append(f"{where} pricing has unexpected key {key!r}")
    if "currency" in pricing and pricing["currency"] != "USD":
        errors.append(f"{where} pricing.currency must be \"USD\"")
    return errors


def _validate_price_rule(where: str, member: Dict[str, Any]) -> List[str]:
    """A zero price belongs to an open-weight release; anything else pays.

    A member that is not open weight needs a price above zero, so a zero price on
    such a member is refused by name.
    """
    pricing = member.get("pricing")
    if not isinstance(pricing, dict):
        return []
    price_in = pricing.get("inputPerM")
    price_out = pricing.get("outputPerM")
    if not _is_num(price_in) or not _is_num(price_out):
        return []
    if _is_open_weight(member.get("license")):
        return []
    if price_in > 0 and price_out > 0:
        return []
    ident = member.get("id", "?")
    license_name = member.get("license")
    return [f"{where} ({ident}): priced {price_in}/{price_out} but its license "
            f"{license_name!r} is not an open-weight release — a zero price is only "
            "allowed on an open-weight member"]


def _price_key(member: Dict[str, Any]) -> Optional[tuple]:
    """(input, output) order key of a priced member, or None when it has no price
    position (a zero price or a malformed block; the shape errors are reported apart)."""
    pricing = member.get("pricing")
    if not isinstance(pricing, dict):
        return None
    price_in, price_out = pricing.get("inputPerM"), pricing.get("outputPerM")
    if not _is_num(price_in) or not _is_num(price_out):
        return None
    if price_in <= 0 or price_out <= 0:
        return None
    return (price_in, price_out)


def _validate_order(label: str, members: List[Any]) -> List[str]:
    """Price order over the members after the lead.

    The lead is the primary anchor's model and stands where the anchor puts it; the
    rest follow in ascending price. A zero-priced open-weight member has no price
    position, so it takes no part in the order and comes last.
    """
    errors: List[str] = []
    last_key: Optional[tuple] = None
    unpriced_seen = False
    for index, member in enumerate(members):
        if index == 0 or not isinstance(member, dict):
            continue
        pricing = member.get("pricing")
        if not isinstance(pricing, dict) or not _is_num(pricing.get("inputPerM")) \
                or not _is_num(pricing.get("outputPerM")):
            continue  # a malformed block is reported by the shape rules
        key = _price_key(member)
        if key is None:
            unpriced_seen = True
            continue
        if unpriced_seen:
            errors.append(f"{label}[{index}] ({member.get('id', '?')}): a priced member "
                          "follows a zero-priced one — the zero-priced members come "
                          "last")
            continue
        if last_key is not None and key < last_key:
            errors.append(f"{label}[{index}] ({member.get('id', '?')}): price order "
                          f"violated — {key} sorts before {last_key} "
                          "(key is (inputPerM, outputPerM))")
        else:
            last_key = key
    return errors


def _default_path() -> Path:
    """The canonical map, located from this file: features/common/data/model-groups.json."""
    return Path(__file__).resolve().parents[3] / "data" / "model-groups.json"


def load_groups(path: Any = None) -> Dict[str, Dict[str, Any]]:
    """Load and validate a map file; return its groups mapping.

    `path` defaults to the canonical map beside this module. Missing, unreadable,
    unparseable, or invariant-violating files raise RegistryInvalid — never a default.
    """
    target = Path(path) if path is not None else _default_path()
    try:
        raw = target.read_text(encoding="utf-8")
    except OSError as exc:
        raise RegistryInvalid([f"cannot read map: {exc.strerror or exc}"],
                              source=target) from exc
    try:
        doc = json.loads(raw)
    except ValueError as exc:
        raise RegistryInvalid([f"invalid JSON: {exc}"], source=target) from exc
    errors = validate_map(doc, source=target)
    if errors:
        raise RegistryInvalid(errors, source=target)
    return doc["groups"]


def _emit_id(member: Dict[str, Any], *, source: Any) -> str:
    """The one field the resolver returns, re-validated before emit."""
    ident = member.get("id")
    if not isinstance(ident, str) or not MODEL_ID_RE.fullmatch(ident):
        raise RegistryInvalid([f"refusing to emit id {ident!r}: must be "
                                "<provider>/<model>"], source=source)
    return ident


def preferred(group: Any, groups: Any = None) -> str:
    """The preferred (index-0) id of `group`. Unknown groups raise UnknownLevel."""
    name = group.strip() if isinstance(group, str) else group
    if groups is None:
        groups = load_groups()
    if not isinstance(groups, dict) or name not in VALID_LEVELS or name not in groups:
        raise UnknownLevel(group)
    entry = groups[name]
    members = entry.get("models") if isinstance(entry, dict) else None
    if not isinstance(members, list) or not members:
        raise RegistryInvalid([f"groups.{name} must carry a non-empty models array"],
                              source="model-groups.json")
    first = members[0]
    if not isinstance(first, dict):
        raise RegistryInvalid([f"groups.{name}.models[0] must be an object"],
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
