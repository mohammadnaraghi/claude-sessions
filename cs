#!/usr/bin/env python3
"""cs - Claude Code session picker.

Lists your recent Claude Code sessions across every project folder, and across
other machines over ssh, shows what each one was about, and resumes the one you
pick: it cd's into the session's directory and runs `claude --resume <id>`,
over ssh when the session lives elsewhere. It also shows which sessions are
running right now and what they are doing, and which ones set up cron jobs.

Usage
  cs                     interactive picker (last 14 days, >= 10 min of work)
  cs 3                   resume the 3rd session in that list
  cs tiktok              filter by text; resumes directly on a single match
                         ("running" and "cron" filter by status, @studio by host)
  cs show 3 | show <id>  details: work per day, prompts, live status, cron jobs
  cs live                running sessions: what they are doing and how far along
  cs jobs                every in-session cron job and its status
  cs path 3              print the session's directory  ->  cd "$(cs path 3)"
  cs -p                  print the list and exit (all rows when piped)
  cs update              replace this script with the latest release

Other machines (over ssh, key auth)
  cs hosts                       list configured hosts and check them
  cs hosts add studio user@host  add a host (label, then ssh destination)
  cs hosts setup studio          copy cs to that host so it can answer
  cs hosts rm studio             remove a host
  Sessions from every host then appear in the list with a host column.
  Resuming one runs claude there over ssh -t, or attaches to its tmux
  window when the session is already running inside tmux.

Options
  -d, --days N           look back N days (default 14)
  -m, --min N            minimum active minutes to be listed (default 10)
  -a, --all              list every session touched in the window
  --sort recent|active   order by last activity (default) or by work time
  --local                this machine only, skip configured hosts
  --host LABEL=DEST      query this host too (or only, with --local)
  --json                 machine-readable output
  -n, --dry-run          print the resume command instead of running it
  --rescan               rebuild the cache from scratch
  --no-fzf               use the built-in picker even if fzf is installed
  -v, --version          print the version
  -- ARGS                pass ARGS to claude, e.g.  cs 2 -- --model opus

Markers   ● session is running (green = busy, yellow = idle)
          ↻ session set up cron jobs (green = a job is still live)

Inside the picker:  3 = resume #3   s 3 = details   /text = filter
                    n / p = next / previous page     q = quit
"""
import argparse
import glob
import json
import math
import os
import re
import shlex
import shutil
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone, time as dtime

VERSION = "0.3.0"
UPDATE_URL = "https://raw.githubusercontent.com/mohammadnaraghi/claude-sessions/main/cs"
CLAUDE_DIR = os.path.expanduser("~/.claude")
PROJECTS = os.path.join(CLAUDE_DIR, "projects")
CACHE_DIR = os.path.expanduser("~/.cache/claude-sessions")
CACHE = os.path.join(CACHE_DIR, "cache.json")
CACHE_VERSION = 4
HOSTS_FILE = os.path.expanduser("~/.config/cs/hosts")
HOME = os.path.expanduser("~")
HOME_RE = re.compile(r"^/(?:Users|home)/[^/]+")
GAP = 30 * 60  # silence longer than this is not counted as work
CRON_LIFETIME = timedelta(days=7)  # recurring in-session jobs auto-expire after this
SSH_OPTS = ["-o", "BatchMode=yes", "-o", "ConnectTimeout=6"]
# Non-interactive ssh shells have a bare PATH; find cs where the installer puts it.
REMOTE_CS = "sh -c 'PATH=\"$HOME/.local/bin:/opt/homebrew/bin:/usr/local/bin:$PATH\"; exec cs \"$@\"' cs"
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
HOSTS = {}


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


def tool_summary(name, inp):
    if name == "Bash":
        s = inp.get("description") or inp.get("command") or ""
    elif name in ("Read", "Edit", "Write", "MultiEdit", "NotebookEdit"):
        s = inp.get("file_path") or ""
    elif name in ("Agent", "Task"):
        s = inp.get("description") or ""
    elif name in ("Grep", "Glob"):
        s = inp.get("pattern") or ""
    elif name == "Skill":
        s = inp.get("skill") or ""
    elif name in ("WebFetch", "WebSearch"):
        s = inp.get("url") or inp.get("query") or ""
    else:
        s = json.dumps(inp)
    return WS_RE.sub(" ", str(s))[:160]


def job_stub(jid):
    return dict(id=jid, cron=None, recurring=True, prompt="", created=None, human="",
                deleted=None, fires=[], error=None)


def new_state():
    return dict(offset=0, first=None, last=None, active=0.0, days={}, user=0,
                assistant=0, tools=0, cwd=None, branch=None, model=None,
                ai_title=None, custom_title=None, first_prompts=[], last_prompts=[],
                last_prompt_ts=None, last_said=None, open_tools={}, ctx_tokens=0,
                todo=None, jobs={}, pending_cron={})


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
        content = msg.get("content")
        if t == "user":
            if o.get("turnOrigin") == "scheduled" and o.get("scheduledTaskId"):
                j = st["jobs"].setdefault(o["scheduledTaskId"], job_stub(o["scheduledTaskId"]))
                j["fires"].append(ts.isoformat())
                if not j["prompt"] and isinstance(content, str):
                    j["prompt"] = WS_RE.sub(" ", content)[:200]
                continue
            if isinstance(content, list):
                for x in content:
                    if not (isinstance(x, dict) and x.get("type") == "tool_result"):
                        continue
                    tid = x.get("tool_use_id")
                    st["open_tools"].pop(tid, None)
                    if tid in st["pending_cron"]:
                        inp = st["pending_cron"].pop(tid)
                        body = x.get("content")
                        s = body if isinstance(body, str) else json.dumps(body)
                        m = re.search(r"(?:job|task) ([0-9a-f]{6,})", s)
                        jid = m.group(1) if m else "?" + tid[-6:]
                        j = st["jobs"].setdefault(jid, job_stub(jid))
                        j.update(cron=inp["cron"], recurring=inp["recurring"], prompt=inp["prompt"],
                                 created=inp["ts"])
                        human = re.search(r"\((.*?)\)", s)
                        j["human"] = human.group(1) if human else ""
                        if not m:
                            j["error"] = WS_RE.sub(" ", s)[:160]
            if o.get("isMeta"):
                continue
            txt = prompt_text(content)
            if not txt:
                continue
            st["user"] += 1
            st["last_prompt_ts"] = ts.isoformat()
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
            usage = msg.get("usage") or {}
            ctx = ((usage.get("input_tokens") or 0) + (usage.get("cache_read_input_tokens") or 0)
                   + (usage.get("cache_creation_input_tokens") or 0))
            if ctx:
                st["ctx_tokens"] = ctx
            if not isinstance(content, list):
                continue
            for x in content:
                if not isinstance(x, dict):
                    continue
                if x.get("type") == "text" and x.get("text", "").strip():
                    st["last_said"] = [ts.isoformat(), WS_RE.sub(" ", x["text"]).strip()[:300]]
                elif x.get("type") == "tool_use":
                    st["tools"] += 1
                    nm, inp = x.get("name"), x.get("input") or {}
                    st["open_tools"][x["id"]] = [ts.isoformat(), nm, tool_summary(nm, inp)]
                    while len(st["open_tools"]) > 8:
                        del st["open_tools"][next(iter(st["open_tools"]))]
                    if nm == "CronCreate":
                        st["pending_cron"][x["id"]] = {
                            "cron": inp.get("cron"), "recurring": inp.get("recurring", True),
                            "prompt": WS_RE.sub(" ", inp.get("prompt") or "")[:200], "ts": ts.isoformat()}
                    elif nm == "CronDelete":
                        jid = inp.get("id") or inp.get("jobId")
                        if jid:
                            st["jobs"].setdefault(jid, job_stub(jid))["deleted"] = ts.isoformat()
                    elif nm == "TodoWrite":
                        todos = inp.get("todos") or []
                        if todos:
                            done = sum(1 for td in todos if td.get("status") == "completed")
                            cur = next((td.get("activeForm") or td.get("content")
                                        for td in todos if td.get("status") == "in_progress"), None)
                            st["todo"] = {"done": done, "total": len(todos), "current": cur}
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
    if not os.path.isdir(PROJECTS):
        return out
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
            out.append(dict(id=ent.name[:-6], path=ent.path, project_dir=proj.name, host=None, **st))
    if dirty:
        os.makedirs(CACHE_DIR, exist_ok=True)
        tmp = CACHE + ".tmp"
        with open(tmp, "w") as f:
            json.dump(cache, f)
        os.replace(tmp, CACHE)
    return out


# ----------------------------------------------------------------- live state

_RUNNING = None


def pid_alive(pid):
    try:
        os.kill(int(pid), 0)
        return True
    except (OSError, TypeError, ValueError):
        return False


def tmux_panes():
    """pane shell pid -> tmux target, so a session inside tmux can be attached to."""
    if not shutil.which("tmux"):
        return {}
    try:
        out = subprocess.run(["tmux", "list-panes", "-a", "-F",
                              "#{pane_pid}\t#{session_name}:#{window_index}.#{pane_index}"],
                             capture_output=True, text=True, timeout=3).stdout
    except (OSError, subprocess.SubprocessError):
        return {}
    panes = {}
    for line in out.splitlines():
        pid, _, target = line.partition("\t")
        if pid.isdigit():
            panes[int(pid)] = target
    return panes


def parent_pid(pid):
    try:
        out = subprocess.run(["ps", "-o", "ppid=", "-p", str(pid)],
                             capture_output=True, text=True, timeout=3).stdout.strip()
        return int(out) if out else None
    except (OSError, subprocess.SubprocessError, ValueError):
        return None


def tmux_target(pid, panes):
    """The tmux window a process lives in, found by walking up its parents."""
    for _ in range(25):
        if not pid or pid <= 1:
            return None
        if pid in panes:
            return panes[pid]
        pid = parent_pid(pid)
    return None


def running():
    """sessionId -> info for Claude Code processes that are alive on this machine right now."""
    global _RUNNING
    if _RUNNING is not None:
        return _RUNNING
    out = {}
    for f in glob.glob(os.path.join(CLAUDE_DIR, "sessions", "*.json")):
        try:
            with open(f) as fh:
                o = json.load(fh)
        except (ValueError, OSError):
            continue
        if o.get("sessionId") and pid_alive(o.get("pid")):
            out[o["sessionId"]] = {"status": o.get("status") or "?", "name": o.get("name"),
                                   "updated": o.get("updatedAt"), "pid": o.get("pid"), "backend": "cli"}
    roster = os.path.join(CLAUDE_DIR, "daemon", "roster.json")
    if os.path.exists(roster):
        try:
            with open(roster) as fh:
                workers = (json.load(fh).get("workers") or {}).values()
        except (ValueError, OSError):
            workers = []
        for w in workers:
            sid = w.get("sessionId")
            if sid and sid not in out and pid_alive(w.get("pid")):
                out[sid] = {"status": "?", "name": None, "updated": None, "pid": w.get("pid"), "backend": "daemon"}
    panes = tmux_panes() if out else {}
    for info in out.values():
        info["tmux"] = tmux_target(info["pid"], panes) if panes else None
    _RUNNING = out
    return out


def daemon_state(sid):
    """State written by daemon-managed sessions (Remote Control, background workers)."""
    p = os.path.join(CLAUDE_DIR, "jobs", sid[:8], "state.json")
    if not os.path.exists(p):
        return None
    try:
        with open(p) as fh:
            o = json.load(fh)
    except (ValueError, OSError):
        return None
    return o if o.get("sessionId", sid) == sid else None


def is_remote(s):
    return bool(s.get("host"))


def running_of(s):
    return s.get("_running") if is_remote(s) else running().get(s["id"])


def daemon_of(s):
    return s.get("_daemon") if is_remote(s) else daemon_state(s["id"])


def doing(s):
    """The tool call still waiting for a result, if any."""
    if is_remote(s):
        return s.get("_doing")
    if not s.get("open_tools"):
        return None
    ts, nm, summary = list(s["open_tools"].values())[-1]
    return f"{nm}: {summary}" if summary else nm


# ----------------------------------------------------------------- cron jobs

def cron_field(spec, lo, hi):
    vals = set()
    for part in spec.split(","):
        step = 1
        if "/" in part:
            part, step = part.split("/", 1)
            step = int(step)
        if part == "*":
            rng = range(lo, hi + 1)
        elif "-" in part:
            a, b = part.split("-", 1)
            rng = range(int(a), int(b) + 1)
        else:
            v = int(part)
            rng = range(v, hi + 1) if step > 1 else range(v, v + 1)
        vals.update(range(rng.start, rng.stop, step))
    return vals


def cron_fires(expr, start, end):
    """How many times a 5-field cron matches in (start, end]; None if unparsable."""
    try:
        m, h, dom, mon, dow = expr.split()
        mins, hours = cron_field(m, 0, 59), cron_field(h, 0, 23)
        doms, mons = cron_field(dom, 1, 31), cron_field(mon, 1, 12)
        dows = {x % 7 for x in cron_field(dow, 0, 7)}
    except (ValueError, AttributeError):
        return None
    n, day = 0, start.date()
    while day <= end.date():
        dmatch, wmatch = day.day in doms, (day.weekday() + 1) % 7 in dows
        if dom != "*" and dow != "*":
            ok = dmatch or wmatch
        else:
            ok = (dmatch if dom != "*" else True) and (wmatch if dow != "*" else True)
        if ok and day.month in mons:
            for hh in hours:
                for mm in mins:
                    t = datetime.combine(day, dtime(hh, mm), tzinfo=start.tzinfo)
                    if start < t <= end:
                        n += 1
        day += timedelta(days=1)
    return n


def job_status(j, alive, session_last):
    """-> (status, fires_text, expiry). Status: live, waiting, done, dead, expired, cancelled."""
    now = datetime.now().astimezone()
    fires = len(j["fires"])
    if not j.get("created"):  # fired here, but created in a transcript we have not seen
        return ("fired" if fires else "?"), (str(fires) if fires else ""), None
    if j.get("error"):
        return "failed", "", None
    created = parse_ts(j["created"]).astimezone()
    if not j.get("recurring", True):
        if j.get("deleted"):
            return "cancelled", "", None
        if fires:
            return "done", "", None
        return ("waiting" if alive else "dead"), "", None
    expiry = created + CRON_LIFETIME
    total = cron_fires(j["cron"], created, expiry) if j.get("cron") else None
    text = f"{fires}/{total} ({100 * fires // total}%)" if total else str(fires)
    if j.get("deleted"):
        status = "cancelled"
    elif alive and now < expiry:
        status = "live"
    elif not alive and (session_last is None or session_last < expiry):
        status = "dead"
    else:
        status = "expired"
    return status, text, expiry


STATUS_COLOR = {"live": "32", "waiting": "33", "done": "", "fired": "", "dead": "2", "expired": "2",
                "cancelled": "2", "failed": "31", "?": "2"}

_JOB_INDEX = None
_ALL_SESSIONS = []


def job_index():
    """Local jobs merged across sessions by id: a continued session splits one job over transcripts."""
    global _JOB_INDEX
    if _JOB_INDEX is not None:
        return _JOB_INDEX
    idx = {}
    for s in _ALL_SESSIONS:
        for jid, j in s["jobs"].items():
            m = idx.setdefault(jid, dict(job_stub(jid), sessions=[]))
            m["sessions"].append(s)
            for k in ("cron", "prompt", "created", "human", "deleted", "error"):
                if j.get(k) and not m.get(k):
                    m[k] = j[k]
            if j.get("created"):
                m["recurring"] = j.get("recurring", True)
            m["fires"] = sorted(set(m["fires"]) | set(j["fires"]))
    _JOB_INDEX = idx
    return idx


def job_home(m):
    """The session a merged job belongs to now: the most recently active one that saw it."""
    return max(m["sessions"], key=lambda s: s["last"])


def merged_status(m):
    home = job_home(m)
    last = parse_ts(home["last"])
    return job_status(m, home["id"] in running(), last.astimezone() if last else None)


def session_jobs(s):
    """[(job, status, fires_text, expiry)] for a session's jobs, newest first."""
    if is_remote(s):
        return s["_jobs"]
    out = []
    for jid in s["jobs"]:
        m = job_index().get(jid)
        if m is None:
            m = dict(s["jobs"][jid], sessions=[s])
        status, text, expiry = merged_status(m)
        out.append((m, status, text, expiry))
    out.sort(key=lambda r: r[0].get("created") or (r[0]["fires"][0] if r[0]["fires"] else ""), reverse=True)
    return out


def once_text(j):
    """A one-shot's cron 'M H D Mo *' rendered as a date."""
    m = re.fullmatch(r"(\d+) (\d+) (\d+) (\d+) \*", (j.get("cron") or "").strip())
    if m:
        mi, hh, dd, mo = map(int, m.groups())
        year = parse_ts(j["created"]).astimezone().year if j.get("created") else datetime.now().year
        try:
            return datetime(year, mo, dd, hh, mi).strftime("%b %d %H:%M")
        except ValueError:
            pass
    return j.get("human") or j.get("cron") or "?"


def job_line(j, status, text, expiry):
    sched = j.get("human") or j.get("cron") or "?"
    if not j.get("created"):
        parts = ["? schedule unknown"]
    elif j.get("recurring", True):
        parts = ["↻ " + sched]
    else:
        parts = ["◦ once " + once_text(j)]
    if j.get("recurring", True) and text:
        parts.append("fires " + text)
    if j["fires"]:
        parts.append("last " + when(j["fires"][-1]))
    parts.append(c("1;" + STATUS_COLOR[status] if STATUS_COLOR[status] else "1", status))
    if status == "live" and expiry:
        parts.append("expires " + when(expiry.isoformat()))
    if status == "dead":
        parts.append("session closed")
    if j.get("error"):
        parts.append(c("33", "create failed"))
    return " · ".join(parts)


# ----------------------------------------------------------------- remote hosts

def load_hosts(extra=None):
    """label -> ssh destination, from ~/.config/cs/hosts plus any --host values."""
    hosts = {}
    if os.path.exists(HOSTS_FILE):
        with open(HOSTS_FILE) as f:
            for line in f:
                line = line.strip()
                if not line or line.startswith("#"):
                    continue
                parts = line.split()
                hosts[parts[0]] = parts[1] if len(parts) > 1 else parts[0]
    for h in extra or []:
        label, _, dest = h.partition("=")
        hosts[label] = dest or label
    return hosts


def save_hosts(hosts):
    os.makedirs(os.path.dirname(HOSTS_FILE), exist_ok=True)
    with open(HOSTS_FILE, "w") as f:
        f.write("# label  ssh-destination   (cs lists and resumes sessions on these hosts over ssh)\n")
        for label, dest in hosts.items():
            f.write(f"{label} {dest}\n")


def ssh(dest, command, timeout=40, tty=False):
    cmd = ["ssh", *([] if tty else SSH_OPTS), *(["-t"] if tty else []), dest, command]
    return subprocess.run(cmd, capture_output=not tty, text=True, timeout=timeout)


def from_json(o, host):
    """A session dict built from another machine's `cs --json` output."""
    s = dict(o)
    s["host"] = host
    for k, v in (("project_dir", ""), ("open_tools", {}), ("todo", None), ("last_said", None),
                 ("ctx_tokens", 0), ("last_prompt_ts", None), ("branch", None), ("model", None)):
        s.setdefault(k, v)
    s["_running"] = o.get("running")
    s["_daemon"] = o.get("daemon")
    s["_doing"] = o.get("doing")
    jobs = o.get("jobs") or []
    s["_jobs"] = [(j, j.get("status", "?"), j.get("fires_text", ""),
                   parse_ts(j["expires"]) if j.get("expires") else None) for j in jobs]
    s["jobs"] = {j["id"]: j for j in jobs}
    return s


def fetch_remote(hosts, days):
    """[(label, sessions, error)] gathered over ssh, hosts queried in parallel."""
    def one(item):
        label, dest = item
        try:
            p = ssh(dest, f"{REMOTE_CS} --json --local -a -d {int(days)}")
        except subprocess.TimeoutExpired:
            return label, [], "timed out"
        except OSError as e:
            return label, [], str(e)
        if p.returncode != 0:
            err = (p.stderr.strip().splitlines() or ["ssh failed"])[-1]
            if "not found" in err:
                err = f"cs is not installed there, run: cs hosts setup {label}"
            return label, [], err
        try:
            data = json.loads(p.stdout)
        except ValueError:
            return label, [], f"unexpected output, run: cs hosts setup {label}"
        return label, [from_json(o, label) for o in data], None
    with ThreadPoolExecutor(max_workers=max(1, len(hosts))) as ex:
        return list(ex.map(one, hosts.items()))


def host_check(dest):
    try:
        p = ssh(dest, f"{REMOTE_CS} --version 2>/dev/null || echo 'cs missing'; "
                      "if command -v tmux >/dev/null 2>&1 || [ -x /opt/homebrew/bin/tmux ]; "
                      "then echo tmux; else echo 'no tmux'; fi", timeout=15)
    except (subprocess.TimeoutExpired, OSError) as e:
        return False, f"unreachable ({e.__class__.__name__})"
    if p.returncode != 0:
        return False, "unreachable: " + (p.stderr.strip().splitlines() or ["ssh failed"])[-1]
    return True, " · ".join(p.stdout.split())


def hosts_setup(label, dest):
    """Copy this script to the host so it can answer `cs --json` and resume sessions."""
    me = os.path.realpath(__file__)
    print(f"copying cs {VERSION} to {label} ({dest}) ...")
    try:
        ssh(dest, "mkdir -p ~/.local/bin", timeout=20)
        subprocess.run(["scp", "-q", *SSH_OPTS, me, f"{dest}:.local/bin/cs"], check=True, timeout=60)
        p = ssh(dest, "chmod +x ~/.local/bin/cs && ~/.local/bin/cs --version && "
                      "(command -v python3 >/dev/null 2>&1 || echo 'warning: python3 missing there') && "
                      "if command -v tmux >/dev/null 2>&1 || [ -x /opt/homebrew/bin/tmux ]; "
                      "then echo 'tmux: installed'; "
                      "else echo 'tmux: not installed there (brew install tmux); sessions in tmux can be attached to'; fi",
                timeout=20)
    except (subprocess.CalledProcessError, subprocess.TimeoutExpired, OSError) as e:
        sys.exit(f"setup failed: {e}")
    print((p.stdout or "") + (p.stderr or ""), end="")
    if p.returncode != 0:
        sys.exit("setup failed")
    print(f"done. `cs` now includes {label}; `cs live` shows what runs there.")


def hosts_cmd(args, hosts):
    if not args:
        if not hosts:
            print("no hosts configured. add one with:  cs hosts add studio user@hostname")
            return
        width = max(len(k) for k in hosts)
        for label, dest in hosts.items():
            ok, msg = host_check(dest)
            print(f"{c('32', '●') if ok else c('31', '●')} {c('1', label.ljust(width))}  {dest}  {c('2', msg)}")
        return
    sub = args[0]
    if sub == "add" and len(args) in (2, 3):
        label, dest = args[1], args[-1]
        hosts[label] = dest
        save_hosts(hosts)
        print(f"added {label} -> {dest}\nnext: cs hosts setup {label}   (copies cs there)")
    elif sub in ("rm", "remove") and len(args) == 2 and args[1] in hosts:
        del hosts[args[1]]
        save_hosts(hosts)
        print(f"removed {args[1]}")
    elif sub == "setup" and len(args) == 2 and args[1] in hosts:
        hosts_setup(args[1], hosts[args[1]])
    else:
        sys.exit("usage: cs hosts | cs hosts add LABEL user@host | cs hosts setup LABEL | cs hosts rm LABEL")


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
    words = query.lower().split()
    hay = None
    for w in words:
        if w.startswith("@"):  # @studio = sessions on that host, @local = this machine
            here = s.get("host") or "local"
            if here != w[1:]:
                return False
            continue
        if w == "running":
            if not running_of(s):
                return False
            continue
        if w == "cron":
            if not s["jobs"]:
                return False
            continue
        if hay is None:
            hay = " ".join([s["id"], project(s), name(s), s.get("ai_title") or "", last_prompt(s),
                            s.get("host") or ""]
                           + [p[1] for p in s["first_prompts"] + s["last_prompts"]]).lower()
        if w not in hay:
            return False
    return True


# ----------------------------------------------------------------- display

def project(s):
    p = s.get("cwd") or s.get("project_dir") or "?"
    return HOME_RE.sub("~", p, count=1)


def short_project(s):
    p = project(s)
    return p if p == "~" else (os.path.basename(p.rstrip("/")) or p)


def name(s):
    """Session name: your /rename title, else Claude's auto title, else first prompt."""
    if s.get("custom_title"):
        return s["custom_title"]
    if s.get("ai_title"):
        return s["ai_title"]
    return fit(s["first_prompts"][0][1], 60) if s["first_prompts"] else "(untitled)"


def subtitle(s):
    """Auto title, shown next to a custom name when both exist and differ."""
    if s.get("custom_title") and s.get("ai_title") and s["ai_title"] != s["custom_title"]:
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


def when(ts_iso):
    return parse_ts(ts_iso).astimezone().strftime("%b %d %H:%M")


def hm(minutes):
    return f"{minutes / 60:.1f}h" if minutes >= 60 else f"{int(minutes)}m"


def ktok(n):
    return f"{n / 1000:.0f}k" if n >= 1000 else str(n)


def fit(s, n):
    return s if len(s) <= n else s[: n - 1] + "…"


def term():
    size = shutil.get_terminal_size((120, 40))
    return size.columns, size.lines


def flags(s):
    """Two marker cells: running, and cron jobs."""
    r = running_of(s)
    a = (c("32", "●") if r.get("status") == "busy" else c("33", "●")) if r else " "
    if s["jobs"]:
        statuses = {st for _, st, _, _ in session_jobs(s)}
        b = c("32", "↻") if statuses & {"live", "waiting"} else c("2", "↻")
    else:
        b = " "
    return a + b


def where(s):
    """'@host' suffix for remote sessions."""
    return c("35", "@" + s["host"]) if is_remote(s) else ""


def print_list(view, start=0, count=None):
    """One row per session, columns sized to the terminal width."""
    cols, _ = term()
    rows = view[start:] if count is None else view[start:start + count]
    if not rows:
        return
    gutter = "   "
    show_proj = cols >= 100
    show_work = cols >= 80
    hw = max([4] + [len(h) for h in HOSTS]) if HOSTS else 0
    nw = min(38, max(len(name(s)) for s in rows))
    pw = min(18, max(len(short_project(s)) for s in rows)) if show_proj else 0
    fixed = 3 + len(gutter) + 2 + len(gutter) + 7 + len(gutter) + nw + len(gutter)
    if hw:
        fixed += hw + len(gutter)
    if show_work:
        fixed += 5 + len(gutter)
    if show_proj:
        fixed += pw + len(gutter)
    lw = max(16, cols - fixed - 1)

    def row(idx, flag, host, last, work, nm, proj, prompt, header=False):
        cells = [(f"{idx:>3}", "1;36"), (flag, None)]
        if hw:
            cells.append((f"{host:<{hw}}", "35"))
        cells.append((f"{last:<7}", "32" if ("m ago" in last or "h ago" in last) else ""))
        if show_work:
            cells.append((f"{work:>5}", ""))
        cells.append((f"{nm:<{nw}}", "1"))
        if show_proj:
            cells.append((f"{proj:<{pw}}", "2"))
        cells.append((prompt, ""))
        if header:
            return c("2", gutter.join(t for t, _ in cells))
        return gutter.join(t if code is None else (c(code, t) if code else t) for t, code in cells)

    print(row("#", "  ", "host", "last", "work", "session", "project", "last prompt", header=True))
    for i, s in enumerate(rows, start + 1):
        print(row(i, flags(s), s.get("host") or "", rel(s["last"]), hm(s["active_win"]), fit(name(s), nw),
                  fit(short_project(s), pw), fit(last_prompt(s), lw)))


def live_block(s, indent="   "):
    """Lines describing what a running session is doing right now."""
    r = running_of(s)
    lines = []
    if r:
        status = r.get("status") or "?"
        ref = s.get("last_prompt_ts") if status == "busy" else s["last"]
        since = rel(ref or s["last"]).replace(" ago", "")
        if status == "busy":
            head = c("32", "busy") + f" for {since}"
        elif status == "idle":
            head = c("33", "idle") + f" since {since}"
        else:
            head = c("33", status) + f" · last activity {since} ago"
        if s.get("ctx_tokens"):
            head += f" · context {ktok(s['ctx_tokens'])} tokens"
        if r.get("tmux"):
            head += f" · tmux {r['tmux']}"
        if r.get("backend") == "daemon":
            head += " · daemon"
        lines.append(head)
    ds = daemon_of(s)
    if ds and (r or ds.get("state") in ("working", "blocked")):
        lines.append(c("2", "state  ") + f"{ds.get('state')} · {fit(ds.get('detail') or '', 150)}")
        prs = [ch for ch in ds.get("children") or [] if ch.get("kind") == "pr"]
        if prs:
            by_repo = {}
            for ch in prs:
                repo = ch.get("href", "").rstrip("/").split("/")[-3] if "/pull/" in ch.get("href", "") else "pr"
                by_repo.setdefault(repo, []).append("#" + str(ch.get("id")))
            lines.append(c("2", "prs    ") + " · ".join(f"{k} {', '.join(v)}" for k, v in by_repo.items()))
    if r:
        ps = s["first_prompts"] + s["last_prompts"]
        if ps:
            lines.append(c("2", "asked  ") + fit(ps[-1][1], 150))
        d = doing(s)
        if d:
            lines.append(c("2", "doing  ") + fit(d, 150))
        if s.get("last_said"):
            lines.append(c("2", "said   ") + fit(s["last_said"][1], 150))
    if s.get("todo"):
        td = s["todo"]
        pct = 100 * td["done"] // td["total"] if td["total"] else 0
        lines.append(c("2", "todo   ") + f"{td['done']}/{td['total']} done ({pct}%)"
                     + (f" · now: {fit(td['current'], 100)}" if td.get("current") else ""))
    return [indent + ln for ln in lines]


def resume_command(s, extra):
    """(argv, description) that resumes or attaches to the session, locally or over ssh."""
    r = running_of(s)
    cwd = s.get("cwd") or "~"
    extra_q = " ".join(shlex.quote(e) for e in extra)
    if is_remote(s):
        dest = HOSTS.get(s["host"], s["host"])
        if r and r.get("tmux"):
            inner = f"tmux attach -t {shlex.quote(r['tmux'])}"
        else:
            inner = f"cd {shlex.quote(cwd)} && claude --resume {s['id']} {extra_q}".rstrip()
        # an interactive login shell on the other side, so claude and tmux are on PATH
        return ["ssh", "-t", dest, f'"$SHELL" -ic {shlex.quote(inner)}'], f"ssh -t {dest} '{inner}'"
    if r and r.get("tmux"):
        verb = "switch-client" if os.environ.get("TMUX") else "attach"
        return ["tmux", verb, "-t", r["tmux"]], f"tmux {verb} -t {r['tmux']}"
    claude = shutil.which("claude") or os.path.expanduser("~/.local/bin/claude")
    return [claude, "--resume", s["id"], *extra], f"cd {project(s)} && claude --resume {s['id']} {extra_q}".rstrip()


def show(s):
    first = parse_ts(s["first"]).astimezone()
    last = parse_ts(s["last"]).astimezone()
    days = " · ".join(f"{d[5:]} {hm(v / 60)}" for d, v in sorted(s["days"].items()) if v >= 60)
    print(c("1", f"Name     {name(s)}") + (c("2", f"   (auto title: {subtitle(s)})") if subtitle(s) else ""))
    print(f"Session  {s['id']}")
    if is_remote(s):
        print(f"Host     {s['host']}  ({HOSTS.get(s['host'], s['host'])})")
    print(f"Project  {project(s)}" + (f"  (branch {s['branch']})" if s.get("branch") else ""))
    print(f"Started  {first:%a %b %d %H:%M}    Last active  {last:%a %b %d %H:%M}  ({rel(s['last'])})")
    print(f"Work     {hm(s['active'] / 60)} total · {days}")
    print(f"Volume   {s['user']} prompts · {s['assistant']} replies · {s['tools']} tool calls"
          + (f" · {s['model']}" if s.get("model") else ""))
    lb = live_block(s, indent="")
    if lb:
        print(c("1", "Now      ") + lb[0])
        for ln in lb[1:]:
            print("         " + ln)
    jobs = session_jobs(s)
    if jobs:
        print(c("1", "Cron jobs"))
        for j, status, text, expiry in jobs:
            print("  " + job_line(j, status, text, expiry))
            if j.get("prompt"):
                print("     " + c("2", fit(j["prompt"], 140)))
    print(c("1", "First prompts"))
    for ts, p in s["first_prompts"]:
        print(f"  {parse_ts(ts).astimezone():%b %d %H:%M}  {fit(p, 200)}")
    if s["last_prompts"]:
        print(c("1", "Latest prompts"))
        for ts, p in s["last_prompts"]:
            print(f"  {parse_ts(ts).astimezone():%b %d %H:%M}  {fit(p, 200)}")
    print(c("1", "Resume   ") + resume_command(s, [])[1])


def live_view(sessions):
    rows = sorted((s for s in sessions if running_of(s)), key=lambda s: s["last"], reverse=True)
    if not rows:
        print("no Claude Code sessions are running right now" + (" on any host" if HOSTS else ""))
        return
    for s in rows:
        r = running_of(s)
        label = r.get("name") or name(s)
        dot = c("32", "●") if r.get("status") == "busy" else c("33", "●")
        print(f"{dot} {c('1', label)} {where(s)}  {c('2', project(s))}")
        if label != name(s):
            print(f"   {c('2', 'name   ')}{name(s)}")
        for ln in live_block(s):
            print(ln)
        for j, st, text, exp in session_jobs(s):
            if st in ("live", "waiting"):
                print("   " + c("2", "cron   ") + job_line(j, st, text, exp))
        print()


def jobs_view(sessions):
    rows = []
    for m in job_index().values():
        status, text, expiry = merged_status(m)
        key = m.get("created") or (m["fires"][0] if m["fires"] else "")
        rows.append((key, job_home(m), m, status, text, expiry))
    for s in sessions:
        if is_remote(s):
            for j, status, text, expiry in s["_jobs"]:
                key = j.get("created") or (j["fires"][0] if j["fires"] else "")
                rows.append((key, s, j, status, text, expiry))
    if not rows:
        print("no in-session cron jobs found in this window (try -d 30)")
        return
    rows.sort(key=lambda r: r[0], reverse=True)
    cols, _ = term()
    label = lambda s: name(s) + (f" @{s['host']}" if is_remote(s) else "")  # noqa: E731
    nw = min(34, max(len(label(s)) for _, s, *_ in rows))
    print(c("2", f"{'status':<9}   {'schedule':<24}   {'fires':<12}   {'last fire':<12}   {'session':<{nw}}   prompt"))
    for key, s, j, status, text, expiry in rows:
        sched = fit(j.get("human") or j.get("cron") or "?", 24)
        if not j.get("recurring", True):
            sched = fit("once " + once_text(j), 24)
        last = when(j["fires"][-1]) if j["fires"] else "-"
        col = STATUS_COLOR[status]
        st = c(col, f"{status:<9}") if col else f"{status:<9}"
        rest = cols - (9 + 3 + 24 + 3 + 12 + 3 + 12 + 3 + nw + 3) - 1
        print(f"{st}   {sched:<24}   {text or ('fired' if j['fires'] else '-'):<12}   {last:<12}   "
              f"{c('1', fit(label(s), nw).ljust(nw))}   {fit(j.get('prompt') or '', max(10, rest))}")


def resume(s, extra, dry_run):
    r = running_of(s)
    if not is_remote(s):
        cwd = s.get("cwd")
        if not cwd or not os.path.isdir(cwd):
            print(c("33", f"warning: {cwd} no longer exists, starting from {HOME}"))
            s = dict(s, cwd=HOME)
    argv, desc = resume_command(s, extra)
    if r and r.get("tmux"):
        print(c("2", f"→ attaching to the tmux window where this session runs: {desc}"))
    else:
        if r:
            print(c("33", "note: this session is already running elsewhere; this starts a second claude on it"))
        print(c("2", f"→ {desc}"))
    if dry_run:
        return
    if not is_remote(s):
        os.chdir(s.get("cwd") or HOME)
    os.execvp(argv[0], argv)


# ----------------------------------------------------------------- pickers

def pick_fzf(view, self_path):
    lines = []
    for s in view:
        r = running_of(s)
        mark = ("●" if r else " ") + ("↻" if s["jobs"] else " ")
        host = f"{(s.get('host') or ''):<8}" if HOSTS else ""
        lines.append("\t".join([s["id"], mark + ("  " + host if host else ""), f"{rel(s['last']):<7}",
                                f"{hm(s['active_win']):>5}", fit(name(s), 38).ljust(38),
                                fit(short_project(s), 18).ljust(18), last_prompt(s)]))
    try:
        res = subprocess.run(
            ["fzf", "--delimiter=\t", "--with-nth=2..", "--no-sort", "--tabstop=1",
             "--header=enter = resume · ctrl-c = quit · ● running · ↻ cron jobs",
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


def pick_builtin(view_all, query, notes):
    flt, page = query, 0
    while True:
        view = [s for s in view_all if matches(s, flt)] if flt else view_all
        _, lines = term()
        per = max(5, lines - 6 - len(notes))
        pages = max(1, math.ceil(len(view) / per))
        page = min(max(page, 0), pages - 1)
        clear()
        for n in notes:
            print(c("33", n))
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
        print(c("2", f"{status} · number = resume · s # = details · /text = filter · q = quit · ● running · ↻ cron"))
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


def to_json(s):
    keys = ("id", "cwd", "project_dir", "host", "branch", "custom_title", "ai_title", "first", "last",
            "active", "active_win", "days", "user", "assistant", "tools", "model", "ctx_tokens", "todo",
            "last_said", "last_prompt_ts", "first_prompts", "last_prompts")
    return {"name": name(s), "last_prompt": last_prompt(s), "running": running_of(s), "doing": doing(s),
            "daemon": daemon_of(s),
            "jobs": [dict({k: v for k, v in j.items() if k != "sessions"}, status=st, fires_text=text,
                          expires=exp.isoformat() if exp else None)
                     for j, st, text, exp in session_jobs(s)],
            **{k: s.get(k) for k in keys}}


def main():
    global _ALL_SESSIONS, HOSTS
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
    ap.add_argument("--local", action="store_true")
    ap.add_argument("--host", action="append", default=[])
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
    rest = a.rest
    if rest == ["update"]:
        self_update()
        return
    if rest[:1] == ["hosts"]:
        hosts_cmd(rest[1:], load_hosts())
        return
    if a.all:
        a.min = 0

    local = load_sessions(a.days, a.rescan)
    _ALL_SESSIONS = local
    sessions = list(local)
    HOSTS = {}
    if a.host or not a.local:
        HOSTS = load_hosts(a.host)
        if a.local:  # --local --host X: only the hosts named on the command line
            named = {h.partition("=")[0] for h in a.host}
            HOSTS = {k: v for k, v in HOSTS.items() if k in named}
    notes = []
    if HOSTS:
        for label, remote, err in fetch_remote(HOSTS, a.days):
            if err:
                notes.append(f"{label}: {err}")
            else:
                sessions += remote
    if notes and not a.json:
        for n in notes:
            print(c("33", n), file=sys.stderr)
    view = select(sessions, a.days, a.min, a.sort)

    if rest == ["live"]:
        pool = select(sessions, a.days, 0, "recent")
        if a.json:
            print(json.dumps([to_json(s) for s in pool if running_of(s)], indent=1))
        else:
            live_view(pool)
        return
    if rest == ["jobs"]:
        pool = select(sessions, a.days, 0, "recent")
        if a.json:
            print(json.dumps([to_json(s) for s in pool if s["jobs"]], indent=1))
        else:
            jobs_view(pool)
        return
    if rest and rest[0] in ("show", "path") and len(rest) == 2:
        s = find(view, sessions, rest[1])
        if rest[0] == "show":
            show(s)
        else:
            print(s.get("cwd") or HOME)
        return

    if a.json:
        print(json.dumps([to_json(s) for s in view], indent=1))
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
        chosen = pick_builtin(hits, query if len(hits) != len(view) else "", notes)
    if chosen:
        resume(chosen, extra, a.dry_run)


if __name__ == "__main__":
    main()
