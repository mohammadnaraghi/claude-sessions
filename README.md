# claude-sessions

`cs` finds and resumes any [Claude Code](https://claude.com/claude-code) session from one place.

Claude Code's own `claude --resume` only shows sessions started in the folder you are in.
`cs` scans every project folder, measures how much real work each session had, shows what it
was about, and resumes the one you pick: it `cd`s into the session's directory and runs
`claude --resume <id>` for you.

```
  #        last       work   session                              project        last prompt
  1   ●    12m ago    3.4h   Checkout flow A/B test               web            compare conversion between variants
  2    ↻   2h ago    29.0h   TikTok attribution                   api            add lookalike audiences to the campaign
  3        1d ago     1.1h   Mandatory Expo updates               mobile         generate the update log for this release
  4   ●↻   2d ago    14.0h   Unresolved support issues            slackbot       set up the daily parse as a cron job
  5        3d ago     45m    LogRocket removal                    mobile         remove it everywhere and open a PR

32 sessions · page 1/4 · n / p to page · number = resume · s # = details · /text = filter · q = quit · ● running · ↻ cron
>
```

`●` marks a session that is running right now (green busy, yellow idle) and `↻` one that set up
cron jobs (green while a job is still live). `cs live` shows what the running ones are doing:

```
● slackbot-59  ~/slackbot
   name   Unresolved support issues
   busy for 2m · context 466k tokens
   asked  full audit of this PR, check the reviewer bot comments
   doing  Bash: run the search fuzz tests
   said   Lint is clean now. Redoing the three commits, pushing, and opening the PR.
   todo   3/8 done (37%) · now: write the fuzz test
   cron   ↻ Every day at 7:13 AM · fires 2/7 (28%) · last Oct 16 07:13 · live · expires Oct 21 11:33
```

Single Python file, no dependencies, nothing leaves your machines.

## Other machines over ssh

Sessions live on the computer that ran them, so `cs` can ask your other machines and show
everything in one list. It needs key-based ssh that works without a prompt.

```sh
cs hosts add studio user@studio.local   # label, then the ssh destination
cs hosts setup studio                   # copies cs to that machine
cs hosts                                # check each host: reachable, cs version, tmux
```

From then on `cs`, `cs live` and `cs jobs` include that host, with a host column in the list.
Picking a remote session runs `claude --resume` there through `ssh -t`. If the session is
already running inside a tmux window, `cs` attaches to that window instead of starting a
second copy, on either machine. `--local` skips the hosts, `--host label=dest` adds one for a
single run, and `cs hosts rm studio` removes one.

Keeping long-running sessions in tmux on an always-on machine is the setup this is built
for: the session, and any cron jobs inside it, survive closing your laptop, and you attach
from anywhere with one keystroke in `cs`.

## Install (macOS and Linux)

```sh
curl -fsSL https://raw.githubusercontent.com/mohammadnaraghi/claude-sessions/main/install.sh | sh
```

This puts one file at `~/.local/bin/cs`, adds that directory to your `PATH` if it is not
already there, and needs only `python3` (3.8 or newer) plus Claude Code itself. Open a new
shell afterwards and type `cs`.

- Update later with `cs update`
- Uninstall with `rm ~/.local/bin/cs && rm -r ~/.cache/claude-sessions`
- Prefer a different directory: `CS_BIN_DIR=/usr/local/bin sh install.sh`

If [fzf](https://github.com/junegunn/fzf) is installed, `cs` uses it automatically for fuzzy
search with a live preview of each session.

## Usage

```
cs                     interactive picker (last 14 days, sessions with 10+ min of work)
cs 3                   resume the 3rd session in that list
cs tiktok              filter by text; resumes directly on a single match
                       (the words "running" and "cron" filter by status)
cs show 3              details: per-day work, prompts, live status, cron jobs
cs live                running sessions: what they are doing and how far along
cs jobs                every in-session cron job with fires, last fire and status
cs path 3              print the session's directory, so  cd "$(cs path 3)"  works
cs -p                  print the list and exit (every row when piped)
cs update              replace the script with the latest release
```

Inside the picker, type a number to resume, `s 3` for details, `/text` to filter,
`n` / `p` to page, and `q` to quit. The list is sized to your terminal, so it never scrolls.

Options:

```
-d, --days N           look back N days (default 14)
-m, --min N            minimum active minutes to be listed (default 10)
-a, --all              list every session touched in the window
--sort recent|active   order by last activity (default) or by hours worked
--local                this machine only, skip configured hosts
--host LABEL=DEST      query this host too (or only, together with --local)
--json                 machine-readable output
-n, --dry-run          print the resume command instead of running it
--rescan               rebuild the cache from scratch
--no-fzf               use the built-in picker even if fzf is installed
-- ARGS                pass ARGS to claude, e.g.  cs 2 -- --model opus
```

## How it works

- Reads the transcripts Claude Code already keeps in `~/.claude/projects/<project>/<session>.jsonl`.
- **Work time** adds up the gaps between consecutive messages, ignoring any pause longer
  than 30 minutes, so a session left open overnight does not count as a night of work.
- **Session name** is the title you set with `/rename`, otherwise the title Claude Code
  generates automatically, otherwise the first prompt.
- **Last prompt** is the last thing you asked, skipping one or two word replies like "yes".
- **Running** sessions come from the per-process files Claude Code keeps in `~/.claude/sessions`
  (checked against live pids). For those, `cs live` reads the end of the transcript: the last
  prompt, the tool call still waiting for a result, the last status line Claude wrote, the
  context size, and the todo list if the session keeps one. Sessions started through Remote
  Control or as background workers also expose a state line and the PRs they opened.
- **Cron jobs** are the `CronCreate` calls a session made. Each fire is a prompt tagged as
  scheduled, so `cs` counts fires against the fires possible in the job's 7-day lifetime.
  A job is `live` only while its session is still running; once the terminal closes it shows
  as `dead`, and `expired` or `cancelled` when that happened first.
- Results are cached in `~/.cache/claude-sessions/cache.json` and updated incrementally, so
  the first run takes a few seconds and later runs are instant.

Resuming runs the real `claude --resume` in the session's original directory, so anything that
worked in the session keeps working.

## Requirements

- macOS or Linux
- Python 3.8+
- Claude Code (the `claude` command on your `PATH`)

## License

MIT
