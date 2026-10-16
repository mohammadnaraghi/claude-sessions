#!/usr/bin/env python3
"""Render the README screenshots from fictional demo data.

Builds two fake ~/.claude folders (this machine and a remote one called "studio"),
runs the real `cs` code against them with the remote side faked, and turns the
coloured terminal output into SVG files in docs/. Nothing here touches your own
~/.claude. Run from the repo root:  python3 docs/make-screenshots.py
"""
import importlib.machinery
import importlib.util
import io
import json
import os
import re
import subprocess
import sys
import tempfile
from datetime import datetime, timedelta

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CS = os.path.join(ROOT, "cs")
OUT = os.path.join(ROOT, "docs")
NOW = datetime.now().astimezone()
COLS = 108

# ----------------------------------------------------------------- demo data


def ts(dt):
    return dt.astimezone().isoformat(timespec="milliseconds").replace("+00:00", "Z")


class Session:
    def __init__(self, sid, cwd, title, custom=None, model="claude-opus-5-5"):
        self.sid, self.cwd, self.title, self.custom, self.model = sid, cwd, title, custom, model
        self.lines = [{"type": "ai-title", "aiTitle": title, "sessionId": sid}]
        if custom:
            self.lines.append({"type": "custom-title", "customTitle": custom, "sessionId": sid})
        self.n = 0

    def rec(self, kind, when, content, **extra):
        self.n += 1
        o = {"type": kind, "timestamp": ts(when), "cwd": self.cwd, "gitBranch": "main",
             "sessionId": self.sid, "uuid": f"{self.sid[:8]}-{self.n:05d}",
             "message": {"role": kind, "content": content}}
        if kind == "assistant":
            o["message"]["model"] = self.model
            o["message"]["usage"] = {"input_tokens": 3, "cache_read_input_tokens": 40000 + 900 * self.n}
        o.update(extra)
        self.lines.append(o)

    def work(self, start, minutes, prompts, said="Done, moving on to the next step.", tool=("Bash", "Run the test suite")):
        """A stretch of back-and-forth: prompts spread over `minutes`, tool calls in between."""
        step = timedelta(minutes=minutes / max(1, len(prompts) * 4))
        t = start
        for p in prompts:
            self.rec("user", t, p)
            t += step
            tid = f"toolu_{self.n:05d}"
            self.rec("assistant", t, [{"type": "text", "text": said},
                                      {"type": "tool_use", "id": tid, "name": tool[0],
                                       "input": {"description": tool[1], "command": "make test"}}])
            t += step
            self.rec("user", t, [{"type": "tool_result", "tool_use_id": tid, "content": "ok"}])
            t += step
            self.rec("assistant", t, [{"type": "text", "text": said}])
            t += step
        return t

    def open_tool(self, when, name, desc):
        tid = f"toolu_{self.n:05d}"
        self.rec("assistant", when, [{"type": "tool_use", "id": tid, "name": name,
                                      "input": {"description": desc}}])

    def todo(self, when, done, total, current):
        todos = [{"content": f"step {i}", "status": "completed"} for i in range(done)]
        todos.append({"content": current, "status": "in_progress", "activeForm": current})
        todos += [{"content": f"step {i}", "status": "pending"} for i in range(total - done - 1)]
        self.rec("assistant", when, [{"type": "tool_use", "id": f"toolu_{self.n:05d}", "name": "TodoWrite",
                                      "input": {"todos": todos}}])

    def cron(self, when, jid, cron, human, prompt, recurring=True, fires=()):
        tid = f"toolu_{self.n:05d}"
        self.rec("assistant", when, [{"type": "tool_use", "id": tid, "name": "CronCreate",
                                      "input": {"cron": cron, "prompt": prompt, "recurring": recurring}}])
        kind = f"recurring job {jid} ({human})" if recurring else f"one-shot task {jid} ({cron})"
        self.rec("user", when + timedelta(seconds=2),
                 [{"type": "tool_result", "tool_use_id": tid, "content": f"Scheduled {kind}. Session-only."}])
        for f in fires:
            self.rec("user", f, prompt, isMeta=True, turnOrigin="scheduled", scheduledTaskId=jid)
            self.rec("assistant", f + timedelta(minutes=2), [{"type": "text", "text": "Report written."}])

    def write(self, claude_dir):
        enc = re.sub(r"[^A-Za-z0-9]", "-", self.cwd)
        d = os.path.join(claude_dir, "projects", enc)
        os.makedirs(d, exist_ok=True)
        with open(os.path.join(d, self.sid + ".jsonl"), "w") as f:
            for o in self.lines:
                f.write(json.dumps(o) + "\n")


def running_file(claude_dir, sid, pid, name, cwd, status):
    os.makedirs(os.path.join(claude_dir, "sessions"), exist_ok=True)
    with open(os.path.join(claude_dir, "sessions", f"{pid}.json"), "w") as f:
        json.dump({"pid": pid, "sessionId": sid, "cwd": cwd, "status": status, "name": name,
                   "updatedAt": int(NOW.timestamp() * 1000)}, f)


def memory(claude_dir, cwd, files):
    enc = re.sub(r"[^A-Za-z0-9]", "-", cwd)
    d = os.path.join(claude_dir, "projects", enc, "memory")
    os.makedirs(d, exist_ok=True)
    index = ["# Memory index", ""]
    for name, (title, hook, body) in files.items():
        with open(os.path.join(d, name), "w") as f:
            f.write(f"---\nname: {name[:-3]}\ndescription: {hook}\n---\n\n{body}\n")
        index.append(f"- [{title}]({name}) — {hook}")
    with open(os.path.join(d, "MEMORY.md"), "w") as f:
        f.write("\n".join(index) + "\n")


def build_local(claude_dir, home, pid):
    h = lambda p: os.path.join(home, p)  # noqa: E731
    ago = lambda **kw: NOW - timedelta(**kw)  # noqa: E731

    s = Session("a1b2c3d4-0000-4000-8000-000000000001", h("code/webapp"), "Checkout flow A/B test")
    t = s.work(ago(hours=3, minutes=20), 170, [
        "we launched the new checkout yesterday, compare conversion between the two variants",
        "split it by device as well", "is the difference significant yet?",
        "add the Safari drop-off to the dashboard", "draft the summary for the team"],
        said="Variant B converts 4% better so far on mobile; desktop is flat. Running the significance check now.")
    s.todo(t, 5, 8, "Writing the significance test")
    s.rec("user", ago(minutes=12), "compare conversion between the two variants over the full week")
    s.rec("assistant", ago(minutes=11), [{"type": "text", "text": "Pulling the full week now. Variant B still leads on mobile; checking whether Safari drop-offs explain the desktop gap."}])
    s.open_tool(ago(minutes=2), "Bash", "Run the checkout integration tests")
    s.write(claude_dir)
    running_file(claude_dir, s.sid, pid, "webapp-12", s.cwd, "busy")

    s = Session("a1b2c3d4-0000-4000-8000-000000000002", h("code/api"), "Nightly usage report")
    t = s.work(ago(days=3, hours=4), 150, [
        "write a script that summarises yesterday's API usage per customer",
        "include error rates", "post it to the #metrics channel",
        "set up the daily report as a cron job"])
    first = (ago(days=3)).replace(hour=9, minute=3, second=0, microsecond=0)
    s.cron(t, "7ec4ad26", "3 9 * * *", "Every day at 9:03 AM", "Daily usage report: run the summary script and post to #metrics",
           fires=[first + timedelta(days=i) for i in range(3) if first + timedelta(days=i) < NOW])
    s.rec("user", ago(hours=1, minutes=5), "also flag any customer whose errors doubled")
    s.rec("assistant", ago(hours=1), [{"type": "text", "text": "Added: customers whose error rate doubled day over day get a warning line. The 9:03 job picks it up tomorrow."}])
    s.write(claude_dir)
    running_file(claude_dir, s.sid, pid + 1, "api-7", s.cwd, "idle")

    s = Session("a1b2c3d4-0000-4000-8000-000000000003", h("code/mobile"), "Mandatory Expo updates")
    for d in (6, 5, 4, 2, 1):
        s.work(ago(days=d, hours=6), 220, [
            "we use expo for updates, find the best way to force an update for some releases",
            "test it on ios too", "don't show a restart button, restart automatically",
            "create the pr", "generate the update log for this release"],
            said="Forced updates work on both platforms; the app restarts itself once the bundle is installed.")
    s.write(claude_dir)

    s = Session("a1b2c3d4-0000-4000-8000-000000000004", h("code/api"), "Search ranking fuzz tests", model="claude-sonnet-5-5")
    s.work(ago(days=2, hours=5), 400, [
        "the new local search returns odd results for short queries, fuzz it",
        "rank exact prefix matches first", "add the fuzz run to CI", "open a pr and summarise the ranking rules"],
        said="Prefix matches now outrank fuzzy ones; the fuzz suite runs 2,000 random queries in CI.")
    s.write(claude_dir)

    s = Session("a1b2c3d4-0000-4000-8000-000000000005", h("code/site"), "Landing page redesign from the proposal", custom="Landing v2")
    s.work(ago(days=3, hours=2), 330, [
        "rebuild the landing page from the design in ~/Downloads/proposal.zip",
        "send 20% of traffic to the new page", "the hero video looks blurry, use the original file",
        "compare sign-ups between the two pages", "do a pr and merge"],
        said="New page is live for 20% of visitors; sign-ups are tracked under the same experiment name.")
    s.write(claude_dir)

    s = Session("a1b2c3d4-0000-4000-8000-000000000006", h("bots/slack"), "Slack export bot")
    t = s.work(ago(days=12, hours=3), 180, [
        "download every message and reply from the support channels",
        "keep image and file links too", "set up a daily parse of these channels"])
    base = ago(days=12).replace(hour=7, minute=13, second=0, microsecond=0)
    s.cron(t, "185232f9", "13 7 * * *", "Every day at 7:13 AM", "Daily Slack export: parse the support channels and update the report",
           fires=[base + timedelta(days=i) for i in range(1, 8)])
    s.rec("user", ago(days=4), "why did the export stop?")
    s.rec("assistant", ago(days=4) + timedelta(minutes=1), [{"type": "text", "text": "The in-session job expired after 7 days. A launchd job or a cloud routine would keep it running."}])
    s.write(claude_dir)

    s = Session("a1b2c3d4-0000-4000-8000-000000000007", h("notes"), "Receipt scanner ideas", model="claude-sonnet-5-5")
    s.work(ago(days=6, hours=1), 45, ["what else could we do with the receipts people already scanned?",
                                      "which of these are quick wins?"],
           said="Three quick wins: spend by store, duplicate detection, and a monthly summary email.")
    s.write(claude_dir)

    s = Session("a1b2c3d4-0000-4000-8000-000000000008", h("code/mobile"), "LogRocket removal")
    s.work(ago(days=8, hours=2), 85, ["remove logrocket everywhere and make sure nothing else breaks",
                                      "run the app once on each platform", "open the pr"],
           said="LogRocket is gone from both apps and the build passes.")
    s.write(claude_dir)

    memory(claude_dir, h("code/webapp"), {
        "checkout-experiment.md": ("Checkout experiment", "variant B, started this week, judge on mobile first",
                                   "The checkout A/B test compares the one-page flow (B) against the stepper (A)."),
        "feedback-no-autodeploy.md": ("Never auto-deploy", "user wants to click deploy themselves",
                                      "**Why:** a bad deploy during a campaign cost a day.\n**How to apply:** open the PR, stop."),
    })
    memory(claude_dir, h("code/mobile"), {
        "forced-updates.md": ("Forced updates", "how mandatory Expo updates are flagged",
                              "A release is mandatory when its changelog starts with `!`."),
    })
    memory(claude_dir, home, {
        "user-profile.md": ("User profile", "solo founder, ships daily, prefers short answers",
                            "Runs a small product company; reviews PRs on the phone."),
    })
    with open(os.path.join(claude_dir, "settings.json"), "w") as f:
        json.dump({"model": "opus", "permissions": {"allow": ["Bash(npm test)", "Bash(git diff *)"]}}, f, indent=2)


def build_remote(claude_dir, home, pid):
    h = lambda p: os.path.join(home, p)  # noqa: E731
    ago = lambda **kw: NOW - timedelta(**kw)  # noqa: E731
    s = Session("b2c3d4e5-0000-4000-8000-000000000001", h("ml/takeoff"), "Blueprint takeoff model")
    for d in range(12, 0, -1):
        s.work(ago(days=d, hours=9), 700, [
            "train the room detector on the new sheet set", "how does the F1 compare to last week?",
            "run the sealed test split", "save the state of the background agents",
            "explain each arm of the a/b"],
            said="Arm B is ahead on the dev set (F1 +5.8); the sealed split runs tonight.")
    t = ago(days=2, hours=1)
    s.cron(t, "c1d2e3f4", "30 9 * * *", "Every day at 9:30 AM", "09:30 takeoff report: compare arms and post the table",
           fires=[(ago(days=1)).replace(hour=9, minute=30), NOW.replace(hour=9, minute=30)] if NOW.hour > 9 else [(ago(days=1)).replace(hour=9, minute=30)])
    tomorrow = NOW + timedelta(days=1)
    s.cron(t + timedelta(minutes=1), "9a8b7c6d", f"20 9 {tomorrow.day} {tomorrow.month} *", "", "Check the overnight training run", recurring=False)
    s.rec("user", ago(minutes=40), "explain each arm of the a/b")
    s.rec("assistant", ago(minutes=38), [{"type": "text", "text": "Three arms, assigned at random per upload: A is the old vector plan, B adds seeded rows, C adds seeded rows plus the closure fix."}])
    s.open_tool(ago(minutes=3), "Bash", "Evaluate arm C on the sealed split")
    s.write(claude_dir)
    running_file(claude_dir, s.sid, pid, "takeoff-3", s.cwd, "busy")

    s = Session("b2c3d4e5-0000-4000-8000-000000000002", h("ml/pipeline"), "Data pipeline cleanup", model="claude-sonnet-5-5")
    s.work(ago(days=2, hours=6), 180, ["dedupe the sheet archive", "drop anything under 300 dpi", "rebuild the index"],
           said="Archive is down to 41k sheets, all above 300 dpi; index rebuilt.")
    s.write(claude_dir)

    memory(claude_dir, h("ml/takeoff"), {
        "arms.md": ("A/B arms", "A old plan, B seeded rows, C seeded + closure fix",
                    "Judge arms on the sealed split only; dev-set wins have fooled us twice."),
        "sheet-sources.md": ("Sheet sources", "which plan sets may be used for training",
                             "Purchased sets and user uploads are fine; the public CubiCasa set is not."),
    })
    memory(claude_dir, home, {
        "user-profile.md": ("User profile", "solo founder, ships daily, prefers short answers",
                            "Runs a small product company; reviews PRs on the phone."),
    })
    with open(os.path.join(claude_dir, "settings.json"), "w") as f:
        json.dump({"model": "opus", "permissions": {"allow": ["Bash(npm test)", "Bash(make *)"]}}, f, indent=2)


# ----------------------------------------------------------------- capture


def load_cs():
    loader = importlib.machinery.SourceFileLoader("cs", CS)
    spec = importlib.util.spec_from_loader("cs", loader)
    mod = importlib.util.module_from_spec(spec)
    loader.exec_module(mod)
    return mod


def capture(cs, argv, cols=COLS, lines=40, picker=False):
    os.environ["COLUMNS"], os.environ["LINES"] = str(cols), str(lines)
    cs.TTY = True
    cs._RUNNING = None
    cs._JOB_INDEX = None
    cs.HOSTS = {}
    buf = io.StringIO()
    old_out, old_argv = sys.stdout, sys.argv
    sys.stdout, sys.argv = buf, ["cs", *argv]
    if picker:
        import builtins
        old_input, old_isatty = builtins.input, sys.stdin.isatty
        builtins.input = lambda *a, **k: (_ for _ in ()).throw(EOFError())
        sys.stdin.isatty = lambda: True
    try:
        cs.main()
    except SystemExit:
        pass
    finally:
        sys.stdout, sys.argv = old_out, old_argv
        if picker:
            builtins.input, sys.stdin.isatty = old_input, old_isatty
    text = buf.getvalue().replace("\033[2J\033[H", "")
    return text.rstrip("\n")


# ----------------------------------------------------------------- ansi -> svg

PALETTE = {"31": "#f38ba8", "32": "#a6e3a1", "33": "#f9e2af", "35": "#cba6f7", "36": "#89dceb"}
FG, DIM, BOLD_FG, BG, CHROME = "#cdd6f4", "#7f849c", "#ffffff", "#1e1e2e", "#181825"
CW, LH, FS, PAD, BAR = 7.6, 19, 12.6, 18, 38


def esc(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def runs(line):
    """[(text, bold, dim, color)] for one line of ANSI text."""
    out, bold, dim, color, pos = [], False, False, None, 0
    for m in re.finditer(r"\033\[([0-9;]*)m", line):
        if m.start() > pos:
            out.append((line[pos:m.start()], bold, dim, color))
        for code in (m.group(1) or "0").split(";"):
            if code == "0":
                bold, dim, color = False, False, None
            elif code == "1":
                bold = True
            elif code == "2":
                dim = True
            elif code in PALETTE:
                color = PALETTE[code]
        pos = m.end()
    if pos < len(line):
        out.append((line[pos:], bold, dim, color))
    return out


def svg(text, title, path, cols=COLS):
    lines = text.split("\n")
    width = int(PAD * 2 + cols * CW)
    height = int(BAR + PAD + len(lines) * LH + PAD)
    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" font-family="SFMono-Regular, Menlo, Consolas, \'Liberation Mono\', monospace" font-size="{FS}">',
             f'<rect width="{width}" height="{height}" rx="10" fill="{BG}"/>',
             f'<path d="M10 0h{width - 20}a10 10 0 0 1 10 10v{BAR - 10}H0V10A10 10 0 0 1 10 0z" fill="{CHROME}"/>',
             '<circle cx="20" cy="19" r="6" fill="#ff5f57"/><circle cx="40" cy="19" r="6" fill="#febc2e"/><circle cx="60" cy="19" r="6" fill="#28c840"/>',
             f'<text x="{width / 2}" y="24" text-anchor="middle" fill="{DIM}" font-size="12">{esc(title)}</text>']
    for i, line in enumerate(lines):
        y = BAR + PAD + (i + 1) * LH - 5
        col = 0
        spans = []
        # a terminal would wrap anything longer than the window; cut it with an ellipsis instead
        segs, used = [], 0
        for seg, bold, dim, color in runs(line):
            if used + len(seg) > cols - 1:
                seg = seg[: max(0, cols - 1 - used)] + "…"
                segs.append((seg, bold, dim, color))
                break
            segs.append((seg, bold, dim, color))
            used += len(seg)
        for seg, bold, dim, color in segs:
            # split at column gaps so columns stay aligned whatever the viewer's font does
            for piece in re.split(r"(?<=\S)(?=\s{2,})", seg):
                lead = len(piece) - len(piece.lstrip(" "))
                body = piece[lead:]
                x = PAD + (col + lead) * CW
                col += len(piece)
                if not body:
                    continue
                fill = color or (DIM if dim else (BOLD_FG if bold else FG))
                attrs = f'x="{x:.1f}" fill="{fill}"'
                if bold:
                    attrs += ' font-weight="600"'
                spans.append(f'<tspan {attrs} xml:space="preserve">{esc(body)}</tspan>')
        parts.append(f'<text y="{y}">{"".join(spans)}</text>')
    parts.append("</svg>")
    with open(path, "w") as f:
        f.write("\n".join(parts))
    print(f"wrote {os.path.relpath(path, ROOT)}  ({len(lines)} lines)")


# ----------------------------------------------------------------- main


def main():
    tmp = tempfile.mkdtemp(prefix="cs-demo-")
    home_local, home_remote = os.path.join(tmp, "home", "sam"), "/home/sam"
    local, remote = os.path.join(tmp, "local-claude"), os.path.join(tmp, "remote-claude")
    sleeper = subprocess.Popen(["sleep", "600"])
    sleeper2 = subprocess.Popen(["sleep", "600"])
    try:
        build_local(local, home_local, sleeper.pid)
        build_remote(remote, home_remote, sleeper2.pid)
        with open(os.path.join(tmp, "local.claude.json"), "w") as f:
            json.dump({"mcpServers": {"github": {"url": "https://api.githubcopilot.com/mcp/"}}}, f)
        with open(os.path.join(tmp, "remote.claude.json"), "w") as f:
            json.dump({"mcpServers": {"github": {"url": "https://api.githubcopilot.com/mcp/"},
                                      "datadog": {"url": "https://mcp.datadoghq.com/"}}}, f)

        # the remote machine's answers, produced by the real script against the remote demo folder
        env = dict(os.environ, CS_CLAUDE_DIR=remote, CS_CLAUDE_JSON=os.path.join(tmp, "remote.claude.json"),
                   CS_HOME=home_remote, CS_CACHE_DIR=os.path.join(tmp, "remote-cache"))
        p = subprocess.run([sys.executable, CS, "--json", "--local", "-a"], env=env, capture_output=True, text=True)
        if p.returncode != 0:
            sys.exit("remote demo failed:\n" + p.stderr)
        remote_json = json.loads(p.stdout)
        for s in remote_json:
            if s.get("running"):
                s["running"]["tmux"] = "main:1.0"
        remote_export = subprocess.run([sys.executable, CS, "_export", "--items", "memory,configs"], env=env,
                                       capture_output=True, check=True).stdout

        os.environ["CS_CLAUDE_DIR"], os.environ["CS_CLAUDE_JSON"] = local, os.path.join(tmp, "local.claude.json")
        os.environ["CS_HOME"], os.environ["CS_CACHE_DIR"] = home_local, os.path.join(tmp, "local-cache")
        cs = load_cs()
        cs.HOSTS_FILE = os.path.join(tmp, "hosts")
        cs.SYNC_STATE_DIR = os.path.join(tmp, "sync")
        cs.CACHE = os.path.join(tmp, "cache.json")
        with open(cs.HOSTS_FILE, "w") as f:
            f.write("studio sam@studio.local\n")

        class R:
            def __init__(self, out, code=0):
                self.stdout, self.stderr, self.returncode = out, b"" if isinstance(out, bytes) else "", code

        def fake_run(cmd, *a, **k):
            tail = cmd[-1] if isinstance(cmd, list) else ""
            if cmd[0] == "ssh" and "_export" in tail:
                return R(remote_export)
            if cmd[0] == "ssh" and "--json" in tail:
                return R(json.dumps(remote_json))
            if cmd[0] == "ssh":
                return R("cs 0.4.0\ntmux\n")
            return R("")
        cs.subprocess.run = fake_run

        home_re = re.escape(home_local)

        def tidy(text):
            return re.sub(home_re, "~", text)

        os.makedirs(OUT, exist_ok=True)
        svg(tidy(capture(cs, [], lines=22, picker=True)), "cs", os.path.join(OUT, "picker.svg"))
        svg(tidy(capture(cs, ["live"])), "cs live", os.path.join(OUT, "live.svg"))
        svg(tidy(capture(cs, ["show", "a1b2c3d4-0000-4000-8000-000000000002"])), "cs show 2", os.path.join(OUT, "show.svg"))
        svg(tidy(capture(cs, ["jobs"])), "cs jobs", os.path.join(OUT, "jobs.svg"))
        svg(tidy(capture(cs, ["sync", "studio", "-n"])), "cs sync studio -n", os.path.join(OUT, "sync.svg"))
        svg(tidy(capture(cs, ["hosts"])), "cs hosts", os.path.join(OUT, "hosts.svg"))
    finally:
        sleeper.kill()
        sleeper2.kill()


if __name__ == "__main__":
    main()
