# claude-sessions

`cs` finds and resumes any [Claude Code](https://claude.com/claude-code) session from one place.

Claude Code's own `claude --resume` only shows sessions started in the folder you are in.
`cs` scans every project folder, measures how much real work each session had, shows what it
was about, and resumes the one you pick: it `cd`s into the session's directory and runs
`claude --resume <id>` for you.

```
  #   last       work   session                              project        last prompt
  1   12m ago    3.4h   Checkout flow A/B test               web            compare conversion between variants
  2   2h ago    29.0h   TikTok attribution                   api            add lookalike audiences to the campaign
  3   1d ago     1.1h   Mandatory Expo updates               mobile         generate the update log for this release
  4   2d ago    14.0h   Unresolved support issues            slackbot       set up the daily parse as a cron job
  5   3d ago     45m    LogRocket removal                    mobile         remove it everywhere and open a PR

32 sessions · page 1/4 · n / p to page · number = resume · s # = details · /text = filter · q = quit
>
```

Single Python file, no dependencies, nothing leaves your machine.

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
cs show 3              details: project, per-day work, first and latest prompts
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
