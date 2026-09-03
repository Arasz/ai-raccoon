"""Thin re-export of the canonical debug_log (P2.2): one copy lives in features/common/hooks.

Executes the canonical module into this module's own namespace, so the sibling-import
pattern (``import debug_log``) and tests patching module globals both hit canonical code.
The canonical sits three levels up beside this copy in the checkout (features/common/hooks/)
and scaffold (.ai-badger/hooks/) shapes, and under features/ in the plugin-cache mirror;
a shape that delivered neither raises ImportError — the signal every consumer's
``except ImportError`` guard already catches — so a missing logger degrades to silence
instead of breaking a hook (D31).
"""
from pathlib import Path as _Path
import importlib.util as _ilu

_here = _Path(__file__).resolve().parents[3]
_candidates = (_here / "hooks" / "debug_log.py",
               _here / "features" / "common" / "hooks" / "debug_log.py")
_canonical = next((p for p in _candidates if p.is_file()), None)
if _canonical is None:
    raise ImportError("no canonical debug_log.py was delivered beside this copy")
exec(compile(_canonical.read_text(encoding="utf-8"), str(_canonical), "exec"), globals())  # pylint: disable=exec-used
