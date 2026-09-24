#!/usr/bin/env python3
"""The shell lexer every PreToolUse guard reads a Bash command through.

`parse(command)` returns each simple command the text would run, nested ones included:
`$(...)`, backticks, `<(...)`, `>(...)`, an unquoted heredoc's substitutions and the script
handed to `sh -c` or `eval`. Each guard ships a byte-identical copy of this file beside
itself (tests/test_shell_parser.py compares them): edit one copy, then copy it over the rest.
"""
from __future__ import annotations

import re
from typing import Dict, List, NamedTuple, Optional, Sequence, Tuple

SHELLS = frozenset(("sh", "bash", "zsh", "dash", "ksh"))
# Words that open or close a compound command; the command they precede is what runs.
RESERVED = frozenset(("!", "{", "}", "if", "then", "else", "elif", "fi", "do", "done",
                      "while", "until"))
ASSIGNMENT = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*=")
# Longest first, so `>>` is never read as `>` followed by `>`.
REDIRECTS = ("&>>", "<<<", "<<-", ">>", ">|", ">&", "&>", "<<", "<&", "<>", ">", "<")
# Nesting deeper than this is not expanded; an inline `$(` that deep does not lex.
MAX_DEPTH = 16

_WORD_END = frozenset(" \t\n;&|()")
_BLIND_TOKEN = re.compile(r"[();<>|&\n]+|[^\s();<>|&]+")


class Wrapper(NamedTuple):
    """A word that runs the command after it: its options that take a value, and how many
    operands it reads before that command (timeout's DURATION)."""
    values: str = ""
    long_values: Tuple[str, ...] = ()
    operands: int = 0


WRAPPERS: Dict[str, Wrapper] = {
    "sudo": Wrapper("ugCDprtTU", ("--user", "--group", "--close-from", "--chdir", "--prompt",
                                  "--role", "--type", "--command-timeout", "--other-user")),
    "doas": Wrapper("uC"),
    "env": Wrapper("uCS", ("--unset", "--chdir", "--split-string")),
    "nice": Wrapper("n", ("--adjustment",)),
    "timeout": Wrapper("sk", ("--signal", "--kill-after"), 1),
    "command": Wrapper(),
    "builtin": Wrapper(),
    "exec": Wrapper("a"),
    "nohup": Wrapper(),
    "time": Wrapper("fo", ("--format", "--output")),
    "stdbuf": Wrapper("ioe", ("--input", "--output", "--error")),
    "xargs": Wrapper("adEILnPs", ("--arg-file", "--delimiter", "--max-lines", "--max-args",
                                  "--max-procs", "--max-chars")),
}


class ShellSyntaxError(ValueError):
    """The text does not lex: an unterminated quote, substitution or redirect."""


def short_flags(args: Sequence[str], stop: str = "") -> str:
    """The letters of every single-dash cluster in *args*, up to `--`.

    A cluster is read up to and including its first letter in *stop*: the rest of it is
    that option's value (`-Mstrict` is `-M strict`, not `-M -s -t -r -i -c -t`).
    """
    letters = []
    for arg in args:
        if arg == "--":
            break
        if arg.startswith("-") and not arg.startswith("--"):
            cluster = arg[1:]
            for index, letter in enumerate(cluster):
                if letter in stop:
                    cluster = cluster[:index + 1]
                    break
            letters.append(cluster)
    return "".join(letters)


def dash_c_index(args: Sequence[str]) -> Optional[int]:
    """Index of the shell's command-string flag, spelled `-c` or combined as `-lc`/`-ec`."""
    for index, token in enumerate(args):
        if token.startswith("-") and not token.startswith("--") and "c" in token[1:]:
            return index
    return None


def _wrapper_end(name: str, wrapper: Wrapper, words: Sequence[str], index: int) -> int:
    """Index just past *wrapper*'s options and operands, or len(words) when it runs nothing."""
    while index < len(words):
        arg = words[index]
        if arg == "--":
            index += 1
            break
        if arg == "-":  # `env -` is `env -i`
            index += 1
            continue
        if not arg.startswith("-"):
            break
        index += 1
        if arg.startswith("--"):
            if "=" not in arg and arg in wrapper.long_values:
                index += 1
            continue
        if name == "command" and set(arg[1:]) & set("vV"):
            return len(words)  # `command -v` looks a program up; it runs nothing
        for position, letter in enumerate(arg[1:], 1):
            if letter in wrapper.values:
                if position == len(arg) - 1:
                    index += 1  # the value is the next word
                break
    return index + wrapper.operands


def program_index(words: Sequence[str]) -> int:
    """Index of the word that runs, past assignments, reserved words and wrappers with their
    options and option values; len(words) when nothing runs."""
    index = 0
    while index < len(words):
        word = words[index]
        if ASSIGNMENT.match(word) or word in RESERVED:
            index += 1
            continue
        name = word.rsplit("/", 1)[-1]
        wrapper = WRAPPERS.get(name)
        if wrapper is None:
            return index
        index = _wrapper_end(name, wrapper, words, index + 1)
    return len(words)


class Command(NamedTuple):
    """One simple command: its words with redirections removed, the index of the word that
    runs, its redirections as (operator, target), and the subshell scope it runs in — a
    child scope is its parent's tuple plus one id."""
    words: Tuple[str, ...]
    start: int
    redirects: Tuple[Tuple[str, str], ...]
    scope: Tuple[int, ...]

    @property
    def program(self) -> str:
        """The basename of the word that runs; "" when nothing does."""
        return self.words[self.start].rsplit("/", 1)[-1] if self.start < len(self.words) else ""

    @property
    def argv(self) -> Tuple[str, ...]:
        """The word that runs and its arguments."""
        return self.words[self.start:]

    @property
    def args(self) -> Tuple[str, ...]:
        """The arguments of the word that runs."""
        return self.words[self.start + 1:]

    @property
    def short_flags(self) -> str:
        """The letters of every single-dash cluster in the arguments, up to `--`."""
        return short_flags(self.args)


def _script(command: Command) -> Optional[str]:
    """The shell text *command* hands to a shell (`sh -c TEXT`) or to `eval`, else None."""
    args = command.args
    if command.program == "eval":
        return " ".join(args) or None
    if command.program in SHELLS:
        index = dash_c_index(args)
        if index is not None and index + 1 < len(args):
            return args[index + 1]
    return None


def _blind(text: str, scope: Tuple[int, ...]) -> List[Command]:
    """*text* split into commands without honouring quotes, so a stray quote hides no word."""
    commands: List[Command] = []
    words: List[str] = []
    redirects: List[Tuple[str, str]] = []
    tokens = _BLIND_TOKEN.findall(text)
    index = 0
    while index < len(tokens):
        token = tokens[index]
        if token in REDIRECTS and index + 1 < len(tokens):
            redirects.append((token, tokens[index + 1]))
            index += 2
            continue
        if token.strip(";&|()<>\n"):
            words.append(token)
        elif words or redirects:
            commands.append(Command(tuple(words), program_index(words), tuple(redirects), scope))
            words, redirects = [], []
        index += 1
    if words or redirects:
        commands.append(Command(tuple(words), program_index(words), tuple(redirects), scope))
    return commands


class _Parser:
    """A recursive-descent lexer over one text; commands land in the shared *out* list."""

    def __init__(self, text: str, out: List[Command], depth: int, blind: bool,
                 counter: List[int]) -> None:
        self.text = text
        self.pos = 0
        self.out = out
        self.depth = depth
        self.blind = blind
        self.counter = counter
        self.committed = len(out)
        self.command_start = 0

    # -------------------------------------------------------------------------- entry
    def run(self, scope: Tuple[int, ...]) -> None:
        """Parse the whole text; a command left incomplete by bad input is dropped, or
        split quote-blind when the parser is blind. Every command before it stands."""
        try:
            self.parse_list(scope, None, top=True)
        except (ShellSyntaxError, RecursionError):
            del self.out[self.committed:]
            if self.blind:
                self.out.extend(_blind(self.text[self.command_start:], scope))

    def child(self, scope: Tuple[int, ...]) -> Tuple[int, ...]:
        """A fresh scope nested in *scope*."""
        self.counter[0] += 1
        return scope + (self.counter[0],)

    def nested(self, text: str, scope: Tuple[int, ...]) -> None:
        """Parse *text* as a script of its own, run in a child of *scope*."""
        if self.depth < MAX_DEPTH:
            _Parser(text, self.out, self.depth + 1, self.blind, self.counter).run(
                self.child(scope))

    def peek(self, offset: int = 0) -> str:
        """The character *offset* past the cursor; "" past the end."""
        at = self.pos + offset
        return self.text[at] if at < len(self.text) else ""

    # -------------------------------------------------------------------------- lists
    def parse_list(self, scope: Tuple[int, ...], stop: Optional[str], top: bool = False) -> None:
        """Commands up to *stop* (the `)` closing a subshell or `$(`), or the end of text."""
        words: List[str] = []
        redirects: List[Tuple[str, str]] = []
        heredocs: List[Tuple[str, bool, bool]] = []
        while True:
            self.skip_blanks()
            char = self.peek()
            ended = True
            if char == "":
                if stop is not None:
                    raise ShellSyntaxError("unterminated substitution")
                self.finish(words, redirects, scope)
                self.commit(top)
                return
            if char == ")" and stop == ")":
                self.pos += 1
                self.finish(words, redirects, scope)
                return
            if char == "#":
                end = self.text.find("\n", self.pos)
                self.pos = len(self.text) if end < 0 else end
                continue
            if char == "\n":
                self.pos += 1
                self.finish(words, redirects, scope)
                self.read_heredocs(heredocs, scope)
            elif self.redirect_op():
                self.redirect(redirects, heredocs, scope)
                ended = False
            elif char in ";&|":
                self.pos += 2 if self.text[self.pos:self.pos + 2] in (
                    ";;", "&&", "||", "|&") else 1
                self.finish(words, redirects, scope)
            elif char == "(":
                self.pos += 1
                self.finish(words, redirects, scope)
                self.parse_list(self.child(scope), ")")
            elif char == ")":  # unmatched: bash refuses it; read it as a separator
                self.pos += 1
                self.finish(words, redirects, scope)
            else:
                ended = False
                start = self.pos
                word = self.word(scope)
                if self.text[start:self.pos].isdigit() and self.peek() in ("<", ">") \
                        and self.redirect_op():
                    self.redirect(redirects, heredocs, scope)  # `2>`: the digits name an fd
                else:
                    words.append(word)
            if ended:
                words, redirects = [], []
                self.commit(top)

    def commit(self, top: bool) -> None:
        """Mark every command so far as complete: bad input later never drops it."""
        if top:
            self.committed = len(self.out)
            self.command_start = self.pos

    def finish(self, words: List[str], redirects: List[Tuple[str, str]],
               scope: Tuple[int, ...]) -> None:
        """Record one simple command, then the script it hands a shell or `eval`, if any."""
        if not words and not redirects:
            return
        command = Command(tuple(words), program_index(words), tuple(redirects), scope)
        self.out.append(command)
        script = _script(command)
        if script is not None:
            self.nested(script, scope)

    def skip_blanks(self) -> None:
        """Skip spaces, tabs and backslash-newline continuations."""
        while True:
            char = self.peek()
            if char in (" ", "\t"):
                self.pos += 1
            elif char == "\\" and self.peek(1) == "\n":
                self.pos += 2
            else:
                return

    # -------------------------------------------------------------------------- redirects
    def redirect_op(self) -> str:
        """The redirection operator at the cursor, or "" (`<(` and `>(` are words)."""
        for op in REDIRECTS:
            if self.text.startswith(op, self.pos):
                if op in ("<", ">") and self.peek(1) == "(":
                    return ""
                return op
        return ""

    def redirect(self, redirects: List[Tuple[str, str]],
                 heredocs: List[Tuple[str, bool, bool]], scope: Tuple[int, ...]) -> None:
        """Consume one redirection and its target; a descriptor duplication names no file."""
        op = self.redirect_op()
        self.pos += len(op)
        self.skip_blanks()
        start = self.pos
        if self.peek() in _WORD_END or self.peek() == "":
            raise ShellSyntaxError(f"{op} without a target")
        target = self.word(scope)
        raw = self.text[start:self.pos]
        if op in ("<<", "<<-"):
            heredocs.append((target, op == "<<-", not any(q in raw for q in "'\"\\")))
            redirects.append((op, target))
            return
        if op in (">&", "<&") and (target.rstrip("-").isdigit() or target == "-"):
            return
        redirects.append((op, target))

    def read_heredocs(self, heredocs: List[Tuple[str, bool, bool]],
                      scope: Tuple[int, ...]) -> None:
        """Consume the bodies of the heredocs opened on the line just ended: they are data,
        except the substitutions an unquoted delimiter lets the shell expand."""
        for delimiter, strip_tabs, expands in heredocs:
            lines = []
            while self.pos < len(self.text):
                end = self.text.find("\n", self.pos)
                end = len(self.text) if end < 0 else end
                line = self.text[self.pos:end]
                self.pos = min(end + 1, len(self.text))
                if (line.lstrip("\t") if strip_tabs else line) == delimiter:
                    break
                lines.append(line)
            if expands:
                body = _Parser("\n".join(lines), self.out, self.depth, self.blind, self.counter)
                body.quoted(scope, closing="")
        heredocs.clear()

    # -------------------------------------------------------------------------- words
    def word(self, scope: Tuple[int, ...]) -> str:
        """One word with quotes removed; a substitution stays as written, and the commands
        inside it are parsed."""
        parts: List[str] = []
        while True:
            char = self.peek()
            if char == "" or char in _WORD_END:
                break
            if char in "<>":
                if self.peek(1) != "(":
                    break
                parts.append(self.substitution(scope, 2))
            elif char == "\\":
                following = self.peek(1)
                self.pos += 2
                if following != "\n":
                    parts.append(following or "\\")
            elif char == "'":
                end = self.text.find("'", self.pos + 1)
                if end < 0:
                    raise ShellSyntaxError("unterminated single quote")
                parts.append(self.text[self.pos + 1:end])
                self.pos = end + 1
            elif char == '"':
                self.pos += 1
                parts.append(self.quoted(scope))
            elif char in "$`":
                parts.append(self.expansion(scope, in_quotes=False))
            else:
                parts.append(char)
                self.pos += 1
        return "".join(parts)

    def quoted(self, scope: Tuple[int, ...], closing: str = '"') -> str:
        """Double-quoted text after its opening quote (or a heredoc body, *closing* "")."""
        parts: List[str] = []
        while True:
            char = self.peek()
            if char == "":
                if closing:
                    raise ShellSyntaxError("unterminated double quote")
                return "".join(parts)
            if closing and char == closing:
                self.pos += 1
                return "".join(parts)
            if char == "\\" and self.peek(1) in ('$', '`', '"', "\\", "\n"):
                if self.peek(1) != "\n":
                    parts.append(self.peek(1))
                self.pos += 2
            elif char in "$`":
                parts.append(self.expansion(scope, in_quotes=True))
            else:
                parts.append(char)
                self.pos += 1

    def expansion(self, scope: Tuple[int, ...], in_quotes: bool) -> str:
        """A `$`-expansion or backtick substitution at the cursor, returned as written."""
        if self.peek() == "`":
            return self.backticks(scope)
        following = self.peek(1)
        if following == "(":
            if self.peek(2) == "(":
                return self.balanced("$((", "(", ")", 2)
            return self.substitution(scope, 2)
        if following == "{":
            return self.balanced("${", "{", "}", 1)
        if following == "'" and not in_quotes:  # $'...' ANSI-C quoting
            start = self.pos
            self.pos += 2
            while self.peek() not in ("'", ""):
                self.pos += 2 if self.peek() == "\\" else 1
            if self.peek() == "":
                raise ShellSyntaxError("unterminated $' quote")
            self.pos += 1
            return self.text[start + 2:self.pos - 1]
        self.pos += 1
        return "$"

    def substitution(self, scope: Tuple[int, ...], opener: int) -> str:
        """`$(...)`, `<(...)` or `>(...)`: its commands run in a child scope."""
        start = self.pos
        self.pos += opener
        if self.depth >= MAX_DEPTH:
            raise ShellSyntaxError("nested too deeply")
        self.depth += 1
        try:
            self.parse_list(self.child(scope), ")")
        finally:
            self.depth -= 1
        return self.text[start:self.pos]

    def backticks(self, scope: Tuple[int, ...]) -> str:
        """A backtick substitution: its text, unescaped, is parsed as a script of its own."""
        start = self.pos
        self.pos += 1
        inner: List[str] = []
        while True:
            char = self.peek()
            if char == "":
                raise ShellSyntaxError("unterminated backtick")
            if char == "\\" and self.peek(1) in ("`", "$", "\\"):
                inner.append(self.peek(1))
                self.pos += 2
                continue
            self.pos += 1
            if char == "`":
                break
            inner.append(char)
        self.nested("".join(inner), scope)
        return self.text[start:self.pos]

    def balanced(self, opener: str, open_char: str, close_char: str, closes: int) -> str:
        """`${...}` or `$((...))`, returned as written; nothing inside runs a command here."""
        start = self.pos
        self.pos += len(opener)
        depth = 1
        while depth > 0 or closes > 1:
            char = self.peek()
            if char == "":
                raise ShellSyntaxError(f"unterminated {opener}")
            self.pos += 1
            if char == "\\":
                self.pos += 1
            elif char == open_char:
                depth += 1
            elif char == close_char:
                depth -= 1
                if depth == 0 and closes > 1:
                    closes -= 1
                    depth = 1
        return self.text[start:self.pos]


def parse(command: str, blind: bool = False) -> List[Command]:
    """Every simple command *command* runs, nested ones included, in the order they start.

    A command that bad input leaves incomplete is dropped — bash would refuse it — and every
    command before it stands. With *blind*, that command is split quote-blind instead.
    """
    out: List[Command] = []
    _Parser(command, out, 0, blind, [0]).run(())
    return out
