#!/usr/bin/env python3
"""cs - Claude Code session picker.

Lists your recent Claude Code sessions across every project folder, shows what
each one was about, and resumes the one you pick: it cd's into the session's
directory and runs `claude --resume <id>`.

Usage
  cs                     interactive picker (last 14 days, >= 10 min of work)
  cs 3                   resume the 3rd session in that list
  cs tiktok              filter by text; resumes directly on a single match
  cs show 3 | show <id>  details for a session (index or id prefix)
  cs path 3              print the session's directory  ->  cd "$(cs path 3)"
  cs -p                  print the list and exit (all rows when piped)
  cs update              replace this script with the latest release

Options
  -d, --days N           look back N days (default 14)
  -m, --min N            minimum active minutes to be listed (default 10)
  -a, --all              list every session touched in the window
  --sort recent|active   order by last activity (default) or by work time
  --json                 machine-readable output
  -n, --dry-run          print the resume command instead of running it
  --rescan               rebuild the cache from scratch
  --no-fzf               use the built-in picker even if fzf is installed
  -v, --version          print the version
  -- ARGS                pass ARGS to claude, e.g.  cs 2 -- --model opus

Inside the picker:  3 = resume #3   s 3 = details   /text = filter
                    n / p = next / previous page     q = quit
"""
import argparse
import json
import math
import os
import re
import shutil
import subprocess
import sys
from datetime import datetime, timedelta, timezone

VERSION = "0.1.0"
UPDATE_URL = "https://raw.githubusercontent.com/mohammadnaraghi/claude-sessions/main/cs"
PROJECTS = os.path.expanduser("~/.claude/projects")
CACHE_DIR = os.path.expanduser("~/.cache/claude-sessions")
CACHE = os.path.join(CACHE_DIR, "cache.json")
CACHE_VERSION = 2
HOME = os.path.expanduser("~")
GAP = 30 * 60  # silence longer than this is not counted as work
SKIP_PREFIX = (
    "<task-notification>", "<system-reminder>", "[Request interrupted",
    "<artifact-content", "<local-command", "<command-name>", "<command-message>",
    "<command-args>", "<bash-input>", "<bash-stdout>", "<bash-stderr>", "Caveat:",
    "This session is being continued", "<ide_opened_file>", "<ide_selection>",
)
PASTE_RE = re.compile(r"</?pasted_content[^>]*>")
REMINDER_RE = re.compile(r"<system-reminder>.*?</system-reminder>", re.S)
WS_RE = re.compile(r"\s+")

TTY = sys.stdout.isatty()


def c(code, s):
    return f"\033[{code}m{s}\033[0m" if TTY else s


def parse_ts(s):
    if not s:
        return None
    try:
        return datetime.fromisoformat(s.replace("Z", "+00:00"))
    except ValueError:
        return None


# ----------------------------------------------------------------- parsing

def clean_part(txt):
    txt = REMINDER_RE.sub(" ", PASTE_RE.sub(" ", txt)).strip()
    if not txt or txt.startswith(SKIP_PREFIX):
        return None
    return WS_RE.sub(" ", txt)


def prompt_text(content):
    """Return the human-written text of a user message, or None."""
    if isinstance(content, str):
        parts = [content]
    elif isinstance(content, list):
        if any(isinstance(x, dict) and x.get("type") == "tool_result" for x in content):
            return None
        parts = [x.get("text", "") for x in content if isinstance(x, dict) and x.get("type") == "text"]
    else:
        return None
    parts = [p for p in (clean_part(p) for p in parts) if p]
    return " ".join(parts) if parts else None


def new_state():
    return dict(offset=0, first=None, last=None, active=0.0, days={}, user=0,
                assistant=0, tools=0, cwd=None, branch=None, model=None,
                ai_title=None, custom_title=None, first_prompts=[], last_prompts=[])


def parse(path, st):
    """Consume any bytes appended to a transcript since st['offset']."""
    with open(path, "rb") as f:
        f.seek(st["offset"])
        data = f.read()
    end = data.rfind(b"\n")
    if end < 0:
        return st
    chunk = data[: end + 1]
    st["offset"] += len(chunk)
    prev = parse_ts(st["last"])
    first = parse_ts(st["first"])
    for line in chunk.split(b"\n"):
        if not line:
            continue
        try:
            o = json.loads(line)
        except ValueError:
            continue
        t = o.get("type")
        if t == "ai-title":
            st["ai_title"] = o.get("aiTitle") or st["ai_title"]
            continue
        if t == "custom-title":
            st["custom_title"] = o.get("customTitle") or st["custom_title"]
            continue
        if t not in ("user", "assistant"):
            continue
        ts = parse_ts(o.get("timestamp"))
        if ts is None:
            continue
        if first is None or ts < first:
            first = ts
        if prev is not None:
            gap = (ts - prev).total_seconds()
            if 0 < gap <= GAP:
                st["active"] += gap
                day = ts.astimezone().strftime("%Y-%m-%d")
                st["days"][day] = st["days"].get(day, 0) + gap
        if prev is None or ts > prev:
            prev = ts
        if st["cwd"] is None and o.get("cwd"):
            st["cwd"] = o["cwd"]
        if o.get("gitBranch"):
            st["branch"] = o["gitBranch"]
        msg = o.get("message") or {}
        if t == "user":
            if o.get("isMeta"):
                continue
            txt = prompt_text(msg.get("content"))
            if not txt:
                continue
            st["user"] += 1
            item = [ts.isoformat(), txt[:400]]
            recent = (st["last_prompts"] or st["first_prompts"])
            if recent and recent[-1][1] == txt[:400]:
                continue
            if len(st["first_prompts"]) < 5:
                st["first_prompts"].append(item)
            else:
                st["last_prompts"] = (st["last_prompts"] + [item])[-4:]
        else:
            st["assistant"] += 1
            if not st["model"] and msg.get("model"):
                st["model"] = msg["model"]
            content = msg.get("content")
            if isinstance(content, list):
                st["tools"] += sum(1 for x in content if isinstance(x, dict) and x.get("type") == "tool_use")
    st["first"] = first.isoformat() if first else None
    st["last"] = prev.isoformat() if prev else None
    return st


def load_sessions(days, rescan=False):
    cache = {}
    if not rescan and os.path.exists(CACHE):
        try:
            with open(CACHE) as f:
                cache = json.load(f)
        except ValueError:
            cache = {}
        if cache.get("__version__") != CACHE_VERSION:
            cache = {}
    cache["__version__"] = CACHE_VERSION
    horizon = (datetime.now() - timedelta(days=days)).timestamp()
    out, dirty = [], False
    for proj in os.scandir(PROJECTS):
        if not proj.is_dir():
            continue
        for ent in os.scandir(proj.path):
            if not ent.name.endswith(".jsonl") or ent.name.startswith("agent-"):
                continue
            s = ent.stat()
            entry = cache.get(ent.path)
            if entry and entry["mtime"] == s.st_mtime and entry["size"] == s.st_size:
                st = entry["st"]
            elif s.st_mtime < horizon and not entry:
                continue  # too old to matter and never parsed
            else:
                st = entry["st"] if entry and entry["size"] <= s.st_size else new_state()
                st = parse(ent.path, st)
                cache[ent.path] = {"mtime": s.st_mtime, "size": s.st_size, "st": st}
                dirty = True
            if not st["first"]:
                continue
            out.append(dict(id=ent.name[:-6], path=ent.path, project_dir=proj.name, **st))
    if dirty:
        os.makedirs(CACHE_DIR, exist_ok=True)
        tmp = CACHE + ".tmp"
        with open(tmp, "w") as f:
            json.dump(cache, f)
        os.replace(tmp, CACHE)
    return out


# ----------------------------------------------------------------- selection

def select(sessions, days, min_active, sort):
    now = datetime.now(timezone.utc)
    cutoff_day = (datetime.now() - timedelta(days=days)).strftime("%Y-%m-%d")
    view = []
    for s in sessions:
        last = parse_ts(s["last"])
        if last is None or last < now - timedelta(days=days) or s["user"] == 0:
            continue
        s["active_win"] = sum(v for d, v in s["days"].items() if d >= cutoff_day) / 60
        s["days_win"] = sorted(d for d, v in s["days"].items() if d >= cutoff_day and v >= 60)
        if s["active_win"] < min_active:
            continue
        view.append(s)
    if sort == "active":
        view.sort(key=lambda s: -s["active_win"])
    else:
        view.sort(key=lambda s: s["last"], reverse=True)
    return view


def matches(s, query):
    q = query.lower()
    hay = " ".join([s["id"], project(s), name(s), s["ai_title"] or "", last_prompt(s)]
                   + [p[1] for p in s["first_prompts"] + s["last_prompts"]]).lower()
    return all(w in hay for w in q.split())


# ----------------------------------------------------------------- display

def project(s):
    p = s["cwd"] or s["project_dir"]
    return p.replace(HOME, "~", 1) if p.startswith(HOME) else p


def short_project(s):
    p = project(s)
    return p if p == "~" else (os.path.basename(p.rstrip("/")) or p)


def name(s):
    """Session name: your /rename title, else Claude's auto title, else first prompt."""
    if s["custom_title"]:
        return s["custom_title"]
    if s["ai_title"]:
        return s["ai_title"]
    return fit(s["first_prompts"][0][1], 60) if s["first_prompts"] else "(untitled)"


def subtitle(s):
    """Auto title, shown next to a custom name when both exist and differ."""
    if s["custom_title"] and s["ai_title"] and s["ai_title"] != s["custom_title"]:
        return s["ai_title"]
    return ""


def last_prompt(s):
    """The last thing you asked, skipping one-word replies like 'yes' or 'resume'."""
    ps = s["first_prompts"] + s["last_prompts"]
    for _, p in reversed(ps):
        if len(p.split()) >= 3:
            return p
    return ps[-1][1] if ps else ""


def rel(ts_iso):
    ts = parse_ts(ts_iso)
    sec = (datetime.now(timezone.utc) - ts).total_seconds()
    if sec < 3600:
        return f"{int(sec // 60)}m ago"
    if sec < 86400:
        return f"{int(sec // 3600)}h ago"
    if sec < 14 * 86400:
        return f"{int(sec // 86400)}d ago"
    return ts.astimezone().strftime("%b %d")


def hm(minutes):
    return f"{minutes / 60:.1f}h" if minutes >= 60 else f"{int(minutes)}m"


def fit(s, n):
    return s if len(s) <= n else s[: n - 1] + "…"


def term():
    size = shutil.get_terminal_size((120, 40))
    return size.columns, size.lines


def print_list(view, start=0, count=None):
    """One row per session, columns sized to the terminal width."""
    cols, _ = term()
    rows = view[start:] if count is None else view[start:start + count]
    if not rows:
        return
    gutter = "   "
    show_proj = cols >= 100
    show_work = cols >= 80
    nw = min(38, max(len(name(s)) for s in rows))
    pw = min(18, max(len(short_project(s)) for s in rows)) if show_proj else 0
    fixed = 3 + len(gutter) + 7 + len(gutter) + nw + len(gutter)
    if show_work:
        fixed += 5 + len(gutter)
    if show_proj:
        fixed += pw + len(gutter)
    lw = max(16, cols - fixed - 1)

    def row(idx, last, work, nm, proj, prompt, header=False):
        cells = [(f"{idx:>3}", "1;36"),
                 (f"{last:<7}", "32" if ("m ago" in last or "h ago" in last) else "")]
        if show_work:
            cells.append((f"{work:>5}", ""))
        cells.append((f"{nm:<{nw}}", "1"))
        if show_proj:
            cells.append((f"{proj:<{pw}}", "2"))
        cells.append((prompt, ""))
        if header:
            return c("2", gutter.join(t for t, _ in cells))
        return gutter.join(c(code, t) if code else t for t, code in cells)

    print(row("#", "last", "work", "session", "project", "last prompt", header=True))
    for i, s in enumerate(rows, start + 1):
        print(row(i, rel(s["last"]), hm(s["active_win"]), fit(name(s), nw),
                  fit(short_project(s), pw), fit(last_prompt(s), lw)))


def show(s):
    first = parse_ts(s["first"]).astimezone()
    last = parse_ts(s["last"]).astimezone()
    days = " · ".join(f"{d[5:]} {hm(v / 60)}" for d, v in sorted(s["days"].items()) if v >= 60)
    print(c("1", f"Name     {name(s)}") + (c("2", f"   (auto title: {subtitle(s)})") if subtitle(s) else ""))
    print(f"Session  {s['id']}")
    print(f"Project  {project(s)}" + (f"  (branch {s['branch']})" if s["branch"] else ""))
    print(f"Started  {first:%a %b %d %H:%M}    Last active  {last:%a %b %d %H:%M}  ({rel(s['last'])})")
    print(f"Work     {hm(s['active'] / 60)} total · {days}")
    print(f"Volume   {s['user']} prompts · {s['assistant']} replies · {s['tools']} tool calls"
          + (f" · {s['model']}" if s["model"] else ""))
    print(c("1", "First prompts"))
    for ts, p in s["first_prompts"]:
        print(f"  {parse_ts(ts).astimezone():%b %d %H:%M}  {fit(p, 200)}")
    if s["last_prompts"]:
        print(c("1", "Latest prompts"))
        for ts, p in s["last_prompts"]:
            print(f"  {parse_ts(ts).astimezone():%b %d %H:%M}  {fit(p, 200)}")
    print(c("1", "Resume   ") + f"cd {project(s)} && claude --resume {s['id']}")


def resume(s, extra, dry_run):
    cwd = s["cwd"]
    if not cwd or not os.path.isdir(cwd):
        print(c("33", f"warning: {cwd} no longer exists, starting from {HOME}"))
        cwd = HOME
    claude = shutil.which("claude") or os.path.expanduser("~/.local/bin/claude")
    cmd = [claude, "--resume", s["id"], *extra]
    print(c("2", f"→ cd {cwd.replace(HOME, '~', 1)} && claude --resume {s['id']} {' '.join(extra)}".rstrip()))
    if dry_run:
        return
    os.chdir(cwd)
    os.execv(claude, cmd)


# ----------------------------------------------------------------- pickers

def pick_fzf(view, self_path):
    lines = []
    for s in view:
        lines.append("\t".join([s["id"], f"{rel(s['last']):<7}", f"{hm(s['active_win']):>5}",
                                fit(name(s), 38).ljust(38), fit(short_project(s), 18).ljust(18),
                                last_prompt(s)]))
    try:
        res = subprocess.run(
            ["fzf", "--delimiter=\t", "--with-nth=2..", "--no-sort", "--tabstop=1",
             "--header=enter = resume · ctrl-c = quit",
             "--preview", f"{sys.executable} {self_path} show {{1}}",
             "--preview-window=down,55%,wrap"],
            input="\n".join(lines), capture_output=True, text=True)
    except FileNotFoundError:
        return None
    if res.returncode != 0 or not res.stdout.strip():
        return None
    sid = res.stdout.split("\t", 1)[0].strip()
    return next((s for s in view if s["id"] == sid), None)


def clear():
    if TTY:
        sys.stdout.write("\033[2J\033[H")
        sys.stdout.flush()


def pick_builtin(view_all, query):
    flt, page = query, 0
    while True:
        view = [s for s in view_all if matches(s, flt)] if flt else view_all
        _, lines = term()
        per = max(5, lines - 6)
        pages = max(1, math.ceil(len(view) / per))
        page = min(max(page, 0), pages - 1)
        clear()
        if flt:
            print(c("2", f"filter: {flt}   (/ clears)"))
        if view:
            print_list(view, page * per, per)
        else:
            print(c("33", "no sessions match"))
        print()
        status = f"{len(view)} sessions"
        if pages > 1:
            status += f" · page {page + 1}/{pages} · n / p to page"
        print(c("2", f"{status} · number = resume · s # = details · /text = filter · q = quit"))
        try:
            ans = input(c("1", "> ")).strip()
        except (EOFError, KeyboardInterrupt):
            print()
            return None
        if ans in ("q", "quit", "exit"):
            return None
        if ans == "":
            continue
        if ans in ("n", "next"):
            page += 1
            continue
        if ans in ("p", "prev", "b", "back"):
            page -= 1
            continue
        if ans.startswith("/"):
            flt, page = ans[1:].strip(), 0
            continue
        m = re.fullmatch(r"(?:s|show)\s+(\d+)", ans)
        if m:
            idx = int(m.group(1))
            if 1 <= idx <= len(view):
                clear()
                show(view[idx - 1])
                print()
                try:
                    input(c("2", "enter to go back "))
                except (EOFError, KeyboardInterrupt):
                    print()
                    return None
            continue
        if ans.isdigit():
            idx = int(ans)
            if 1 <= idx <= len(view):
                return view[idx - 1]
            continue
        flt, page = ans, 0


# ----------------------------------------------------------------- main

def find(view, sessions, key):
    """Resolve an index (into view) or an id prefix (any session)."""
    if key.isdigit():
        idx = int(key)
        if 1 <= idx <= len(view):
            return view[idx - 1]
        sys.exit(f"no session #{key}; the list has {len(view)} entries")
    hits = [s for s in sessions if s["id"].startswith(key)]
    if len(hits) == 1:
        return hits[0]
    sys.exit(f"{'no' if not hits else 'ambiguous'} session id '{key}'")


def print_mode(hits):
    """-p: fit the screen on a terminal, print everything when piped."""
    if not TTY:
        print_list(hits)
        return
    _, lines = term()
    per = max(5, lines - 4)
    print_list(hits, 0, per)
    if len(hits) > per:
        print(c("2", f"… {len(hits) - per} more · run cs to page or filter, or cs -p | cat for all"))


def self_update():
    import urllib.request
    path = os.path.realpath(__file__)
    try:
        data = urllib.request.urlopen(UPDATE_URL, timeout=20).read()
    except Exception as e:  # noqa: BLE001
        sys.exit(f"download failed: {e}")
    compile(data, path, "exec")  # refuse to install a broken script
    m = re.search(rb'^VERSION = "([^"]+)"', data, re.M)
    new = m.group(1).decode() if m else "?"
    if new == VERSION:
        print(f"cs {VERSION} is already the latest")
        return
    tmp = path + ".new"
    with open(tmp, "wb") as f:
        f.write(data)
    os.chmod(tmp, 0o755)
    os.replace(tmp, path)
    print(f"cs updated {VERSION} -> {new}")


def main():
    argv = sys.argv[1:]
    extra = []
    if "--" in argv:
        i = argv.index("--")
        argv, extra = argv[:i], argv[i + 1:]
    ap = argparse.ArgumentParser(prog="cs", add_help=False)
    ap.add_argument("rest", nargs="*")
    ap.add_argument("-d", "--days", type=int, default=14)
    ap.add_argument("-m", "--min", type=float, default=10)
    ap.add_argument("-a", "--all", action="store_true")
    ap.add_argument("--sort", choices=["recent", "active"], default="recent")
    ap.add_argument("-p", "--print", action="store_true")
    ap.add_argument("--json", action="store_true")
    ap.add_argument("-n", "--dry-run", action="store_true")
    ap.add_argument("--rescan", action="store_true")
    ap.add_argument("--no-fzf", action="store_true")
    ap.add_argument("-h", "--help", action="store_true")
    ap.add_argument("-v", "--version", action="store_true")
    a = ap.parse_args(argv)
    if a.help:
        print(__doc__.strip())
        return
    if a.version:
        print(f"cs {VERSION}")
        return
    if a.rest == ["update"]:
        self_update()
        return
    if a.all:
        a.min = 0

    sessions = load_sessions(a.days, a.rescan)
    view = select(sessions, a.days, a.min, a.sort)

    rest = a.rest
    if rest and rest[0] in ("show", "path") and len(rest) == 2:
        s = find(view, sessions, rest[1])
        if rest[0] == "show":
            show(s)
        else:
            print(s["cwd"] or HOME)
        return

    if a.json:
        keys = ("id", "cwd", "branch", "custom_title", "ai_title", "first", "last", "active", "active_win",
                "days", "user", "assistant", "tools", "model", "first_prompts", "last_prompts")
        print(json.dumps([{"name": name(s), "last_prompt": last_prompt(s), **{k: s.get(k) for k in keys}}
                          for s in view], indent=1))
        return

    query = " ".join(rest)
    if query.isdigit():
        resume(find(view, sessions, query), extra, a.dry_run)
        return
    if query:
        hits = [s for s in view if matches(s, query)]
        if len(hits) == 1 and not a.print:
            resume(hits[0], extra, a.dry_run)
            return
    else:
        hits = view

    if a.print:
        print_mode(hits)
        return
    if not hits:
        sys.exit(f"no sessions match '{query}' in the last {a.days} days (try -d 30 or -a)")
    if not sys.stdin.isatty():
        print_mode(hits)  # no terminal to pick from: behave like -p
        return

    if not a.no_fzf and shutil.which("fzf"):
        chosen = pick_fzf(hits, os.path.abspath(__file__))
    else:
        chosen = pick_builtin(hits, query if len(hits) != len(view) else "")
    if chosen:
        resume(chosen, extra, a.dry_run)


if __name__ == "__main__":
    main()
