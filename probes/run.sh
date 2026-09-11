#!/usr/bin/env bash
#
# The gate. Every server bump has to survive this before it reaches main.
#
# cases.jsonl is one flat JSON object per line with exactly four string fields
# (name, args, exit, expect) so it can be parsed with sed alone -- no jq, no python,
# so the same script runs on a GitHub runner and in Git Bash on Windows. Inside
# `expect`, ' stands for " and | separates substrings that must all appear in the
# combined stdout+stderr of the command. That separator means an expectation can never
# quote a rendered `cslq outline` row, whose gutter is also | -- pasting one in silently
# becomes two weaker substring matches. Assert bare declarations instead. A line starting
# with # is a comment: the four fields leave nowhere to say why a row opts out of the
# session, which is the one thing about a row that a reader cannot infer from it.
set -uo pipefail

cd "$(dirname "$0")/.."
root=$(pwd)
# What `cslq` prints for a path it did not get from us. MSYS hands a native .NET process
# Windows paths, so `pwd` alone would not match the manifest directory `cslq restore` names.
root_abs=$( { pwd -W 2>/dev/null || pwd; } )
# cslq talks to the shared daemon by default, so scope this run to a daemon of its own.
# Without the pipe name the suite would inherit whatever daemon the developer's session
# left running -- a stale workspace could make the gate lie, and the `cslq ready` below
# would stop being a cold load. Keepalive is short because this daemon is disposable:
# the countdown only starts once the last case disconnects.
export ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME="cslq-probe-$$"
export ROSLYN_LANGUAGE_SERVER_DAEMON_KEEPALIVE=60
# And cslq sessions of its own, for the same reasons: a session the developer left running
# holds a workspace this suite did not build, and one this suite starts must not outlive it.
# The EXIT trap kills them by the pid each one logs; the short keepalive is the backstop.
#
# A name per attach rather than one for the suite. CSLQ_SESSION_PIPE_NAME overrides the
# derived name outright, so a single exported value would put every root on one pipe -- the
# first session to start would then decline every other root's requests and two thirds of the
# cases would run through the fallback instead of through the session that is now the
# default. `pipe_for` below derives one name per (root, daemon) pair under this prefix, which
# is also what lets the trap find every session this run started.
#
# Nothing may run cslq without setting it: without it the derivation is the real one, and the
# suite would leave a session behind on the pipe a developer's own calls use.
SESSION_PREFIX="cslq-probe-session-$$"
export CSLQ_SESSION_KEEPALIVE=60

# The temp directory Session.LogPath writes into: %TEMP% on Windows and $TMPDIR (else /tmp)
# elsewhere. %TEMP% is a Windows path, which bash cannot use until cygpath has turned it round.
session_dir() {
  if command -v cygpath >/dev/null 2>&1 && [ -n "${TEMP:-}" ]; then
    cygpath -u "$TEMP"
  else
    printf '%s' "${TMPDIR:-/tmp}"
  fi
}

session_log_path() { printf '%s/cslq-session-%s.log' "$(session_dir)" "$1"; }

# The session name for an argument list: one attach per root and per whether the Roslyn daemon
# is used, which is exactly the key Session.PipeName derives from. Anything else in the args --
# --max, --json, a symbol -- is answered by the same session.
pipe_for() {
  pf_root=cwd
  pf_daemon=d
  pf_prev=""
  for pf_tok in $1; do
    [ "$pf_prev" = "--root" ] && pf_root=$pf_tok
    [ "$pf_tok" = "--no-daemon" ] && pf_daemon=n
    pf_prev=$pf_tok
  done
  printf '%s-%s-%s' "$SESSION_PREFIX" "$pf_daemon" \
    "$(printf '%s' "$pf_root" | tr -c 'A-Za-z0-9' '_')"
}


log() { printf '\n==> %s\n' "$*"; }

# `date +%s%N` is GNU-only -- BSD date on macos-latest prints a literal N -- and $EPOCHREALTIME
# needs bash 5, which /bin/bash on macOS is not. perl is on all three platforms and is already
# what this script uses for the in-place edit below.
now_ms() { perl -MTime::HiRes=time -e 'printf "%.0f", time * 1000'; }

log "dotnet tool restore"
dotnet tool restore || exit 1

# The unit tests first: they cover the pure logic below the transport -- sentinel
# inference, argument parsing, path and URI rendering -- and cost under a second, so a
# regression there fails here rather than after the cold load below.
#
# No --nologo. In the MTP mode of `dotnet test` that global.json opts into, it makes the
# run discover zero tests and exit 5 -- loudly, but for a reason that reads as a broken
# test project rather than a bad flag.
log "dotnet test"
dotnet test --project tests/Cslq.Tests/Cslq.Tests.csproj || exit 1

# The server restores on its own as part of its design-time build (measured: a never-restored
# solution is ready in 7 s and has an obj/project.assets.json afterwards), so this is here to
# make the gate deterministic rather than to make it work: restoring up front keeps the cold
# `ready` below a measurement of load time, and a restore that *fails* is loud here and silent
# there -- every project would simply load empty.
log "dotnet restore (fixture)"
dotnet restore fixture/Fixture.slnx --nologo -v q || exit 1

# The analyzer has to exist as a built assembly before the server loads Core, or the
# generator contributes nothing -- no error, no diagnostic, the generated symbol simply
# is not there. Debug is pinned because the design-time build resolves the analyzer from
# Gen/bin/Debug; building it Release would leave that path stale or empty.
#
# Core, not Gen: the analyzer ProjectReference makes this build Gen first anyway, and
# compiling Core is what arms its <WarningsAsErrors>CS9057</WarningsAsErrors> -- the guard
# against the analyzer being built against a newer compiler than the one loading it, which
# otherwise degrades to the same silent nothing. Never the solution: the deliberate type
# error for `cslq diag` is deliberately kept out of Core so this step stays green.
log "build fixture generator + Core"
dotnet build fixture/Core/Core.csproj -c Debug --nologo -v q || exit 1

# The second fixture: two projects consuming one generator, which is the shape fixture/
# cannot hold. Its generated documents differ only by the project that consumed them --
# the generated URI names the generator, never the consumer -- so it is the only thing
# that catches the label collapsing back to one. Both consumers are built for the same
# reason Core is: an unbuilt analyzer contributes nothing, silently.
#
# It also holds Multi, the only multi-targeted project in either fixture: net10.0;net9.0,
# with a type in each #if branch and a CS0029 that exists only in the net9.0 context.
# The restore below covers it through the solution; nothing builds it, for the same reason
# nothing builds fixture/App -- one of its two contexts does not compile, on purpose.
log "dotnet restore (fixture2)"
dotnet restore fixture2/Fixture2.slnx --nologo -v q || exit 1

log "build fixture2 generator + consumers"
dotnet build fixture2/Alpha/Alpha.csproj -c Debug --nologo -v q || exit 1
dotnet build fixture2/Beta/Beta.csproj -c Debug --nologo -v q || exit 1

log "build cslq"
dotnet build src/Cslq/Cslq.csproj -c Release --nologo -v q || exit 1

CSLQ="$root/src/Cslq/bin/Release/net10.0/cslq"
[ -x "$CSLQ" ] || CSLQ="$CSLQ.exe"
[ -x "$CSLQ" ] || { echo "cslq not found at $CSLQ" >&2; exit 1; }
# The same binary as a Windows path, for the legs that hand it to a .NET harness rather
# than running it from bash.
CSLQ_WIN="${CSLQ/#$root/$root_abs}"

# Readiness is asserted before any case runs: project load is async and a query fired
# too early returns empty results, not an error, so a naive probe reports a false pass.
log "cslq ready"
start=$(date +%s)
CSLQ_SESSION_PIPE_NAME=$(pipe_for "--root fixture") \
  "$CSLQ" ready --root fixture --timeout 300 || exit 1
printf 'cold ready: %ss\n' "$(( $(date +%s) - start ))"

log "cases"
pass=0
fail=0

while IFS= read -r line || [ -n "$line" ]; do
  [ -z "${line// /}" ] && continue
  # A `#` line is a comment. The four fields leave nowhere to say why a row carries
  # --no-session, and "the case is opted out of the default" is exactly the thing a reader
  # needs told: every one of them is a case whose subject is behaviour a warm session no
  # longer has, and without a sentence saying so the flag reads as a workaround.
  case "$line" in \#*) continue ;; esac

  name=$(printf '%s' "$line" | sed -n 's@.*"name":"\([^"]*\)".*@\1@p')
  args=$(printf '%s' "$line" | sed -n 's@.*"args":"\([^"]*\)".*@\1@p')
  want_exit=$(printf '%s' "$line" | sed -n 's@.*"exit":"\([^"]*\)".*@\1@p')
  expect=$(printf '%s' "$line" | sed -n 's@.*"expect":"\([^"]*\)".*@\1@p')

  if [ -z "$name" ] || [ -z "$args" ]; then
    printf 'MALFORMED %s\n' "$line"
    fail=$((fail + 1))
    continue
  fi

  # shellcheck disable=SC2086 -- args is a deliberately word-split argument list.
  out=$(CSLQ_SESSION_PIPE_NAME=$(pipe_for "$args") "$CSLQ" $args 2>&1)
  got_exit=$?

  ok=1
  reason=""
  if [ "$got_exit" != "$want_exit" ]; then
    ok=0
    reason="exit $got_exit, wanted $want_exit"
  fi

  old_ifs=$IFS
  IFS='|'
  for want in $expect; do
    want=${want//\'/\"}
    case "$out" in
      *"$want"*) ;;
      *) ok=0; reason="${reason:+$reason; }missing: $want" ;;
    esac
  done
  IFS=$old_ifs

  if [ "$ok" = 1 ]; then
    printf 'PASS  %s\n' "$name"
    pass=$((pass + 1))
  else
    printf 'FAIL  %s (%s)\n' "$name" "$reason"
    printf '%s\n' "$out" | sed 's/^/      | /'
    fail=$((fail + 1))
  fi
done < probes/cases.jsonl

# The framework `def`, which no cases.jsonl row can pin: the two things that went wrong are
# an absence (the machine-absolute MetadataAsSource path must not appear) and a duration (the
# decompilation guard used to re-ask 40 times over its whole 10 s budget and then return the
# answer it already had on the first). `expect` can only require a substring, and the message
# alone passes just as well after twelve seconds.
#
# Warm by construction: `def-framework-member` above is what makes Roslyn write the decompiled
# document, which cold costs about six seconds on its own. The bound is 10 s because that is
# exactly what the old guard burned -- a regression cannot come in under it.
log "framework def"
fw_start=$(date +%s)
fw_log=$(mktemp)
CSLQ_SESSION_PIPE_NAME=$(pipe_for "--root fixture")   "$CSLQ" def App/Program.cs:9:17 --root fixture > "$fw_log" 2>&1
rc=$?
fw_elapsed=$(( $(date +%s) - fw_start ))
out=$(cat "$fw_log")
rm -f "$fw_log"

ok=1
[ "$rc" = 0 ] || ok=0
[ "$fw_elapsed" -lt 10 ] || ok=0
case "$out" in
  *"<metadata>/System.Console/Console.cs:"*) ;;
  *) ok=0 ;;
esac
case "$out" in
  *MetadataAsSource*) ok=0 ;;
esac
if [ "$ok" = 1 ]; then
  printf 'PASS  %s\n' "framework-def-is-labelled-and-does-not-stall"
  pass=$((pass + 1))
else
  printf 'FAIL  %s (exit %s after %ss, wanted 0 under 10s and a <metadata>/ label)\n' \
    "framework-def-is-labelled-and-does-not-stall" "$rc" "$fw_elapsed"
  printf '%s\n' "$out" | sed 's/^/      | /'
  fail=$((fail + 1))
fi

# The only case that mutates the fixture, and the only one that needs a server to outlive
# an invocation: rename what the generator keys on, then ask a *fresh* client whether the
# generated symbol went away. It is a shell block rather than a cases.jsonl row for both
# reasons -- a row is one invocation and cannot restore what it changed.
#
# Absence is also what an unloaded workspace looks like, so every leg gates on
# `--sentinel Cheer`, which the rename does not touch: the workspace is provably loaded
# before absence is concluded. Run the legs in order -- only restore-and-present proves
# the daemon is still live and answering rather than quietly stuck.
log "source-generator staleness"
greeter=fixture/Core/Greeter.cs
greeter_saved=$(mktemp)
cp "$greeter" "$greeter_saved"
# One EXIT hook for the whole run. The rename below has to be undone even on an interrupt --
# see CLAUDE.md -- and the packaged-tool leg's throwaway tree is cleaned by the same hook.
install_tmp=""
# The two-solutions leg's throwaway root, cleaned by the same hook.
ts_tmp=""
# Set only while the exhausted-candidate leg below has its two-project tree on disk.
ec_tmp=""
# Set only while the failed-design-time-build leg below has its pinned-SDK tree on disk.
gj_tmp=""
# Set only while the restore leg below has the tool resolver cache entry moved aside. Leaving
# it moved would make every later `dotnet tool run` on this machine re-resolve the pin.
cache_saved=""
cache_entry=""
# Every session this suite started, ended by the pid each one logs on its first line. Found by
# globbing rather than by name because there is one per attach now: the prefix is what makes
# them ours, and a session a developer left running is on a name this never matches. taskkill
# on Windows, where that pid is a Windows one and MSYS `kill` speaks its own pid space; plain
# kill everywhere else.
kill_sessions() {
  for session_log in "$(session_dir)/cslq-session-$SESSION_PREFIX-"*.log; do
    [ -f "$session_log" ] || continue
    session_pid=$(sed -n 's/^cslq session [^ ]* pid \([0-9]*\) .*/\1/p' "$session_log" | tail -1)
    [ -n "$session_pid" ] || continue
    if command -v taskkill >/dev/null 2>&1; then
      taskkill //F //PID "$session_pid" >/dev/null 2>&1
    else
      kill "$session_pid" 2>/dev/null
    fi
  done
  return 0
}

cleanup() {
  kill_sessions
  cp "$greeter_saved" "$greeter"
  rm -f "$greeter_saved"
  [ -n "$install_tmp" ] && rm -rf "$install_tmp"
  [ -n "$ts_tmp" ] && rm -rf "$ts_tmp"
  [ -n "$ec_tmp" ] && rm -rf "$ec_tmp"
  [ -n "$gj_tmp" ] && rm -rf "$gj_tmp"
  # The restore writes a fresh entry; the saved one is the developer's, and it covers every
  # manifest on the machine rather than only this repository's.
  if [ -n "$cache_saved" ] && [ -e "$cache_saved" ]; then
    rm -rf "$cache_entry"
    mv "$cache_saved" "$cache_entry"
  fi
  return 0
}
trap cleanup EXIT

# The daemon refreshes off its own file watcher, so both directions are eventually
# consistent -- poll rather than trust the first answer. Each absent attempt already
# costs the ~10s grace MatchSymbolsAsync gives a symbol before calling it missing.
await_generated() {
  deadline=$(( $(date +%s) + 90 ))
  while :; do
    out=$(CSLQ_SESSION_PIPE_NAME=$(pipe_for "--root fixture")       "$CSLQ" def Fixture.Core.Generated.BuildInfo.Stamp --root fixture --sentinel Cheer 2>&1)
    rc=$?
    if [ "$1" = present ] && [ "$rc" = 0 ]; then
      case "$out" in *"BuildInfo.g.cs"*) return 0 ;; esac
    fi
    if [ "$1" = absent ] && [ "$rc" = 1 ]; then
      case "$out" in *"no symbol matched"*) return 0 ;; esac
    fi
    if [ "$(date +%s)" -ge "$deadline" ]; then
      printf '%s\n' "$out" | sed 's/^/      | /'
      return 1
    fi
  done
}

leg() {
  if await_generated "$2"; then
    printf 'PASS  %s\n' "$1"
    pass=$((pass + 1))
  else
    printf 'FAIL  %s (generated symbol never became %s)\n' "$1" "$2"
    fail=$((fail + 1))
  fi
}

leg staleness-baseline-present present
# perl, not `sed -i`: BSD sed takes -i's argument as a mandatory backup suffix, so on macOS
# this reads the script as the suffix and fails, and BSD sed has no \b either. perl is on
# ubuntu-latest, macos-latest and Git Bash alike, and its \b means the same thing everywhere.
perl -pi -e 's/\bGreeter\b/GreeterRenamed/g' "$greeter"
leg staleness-after-rename-absent absent
cp "$greeter_saved" "$greeter"
leg staleness-after-restore-present present

# The silent non-daemon fallback, forced. The thin client falls back when it times out
# waiting for its startup mutex (about 20 s), so holding that mutex is the entire trigger
# -- see probes/hold-mutex.cs for why that needs a .NET process. A distinct pipe name is
# required: the mutex only guards check-server-then-launch, so a client that finds a daemon
# already listening never contends for it.
#
# Both halves of the assertion matter. Exit 0 pins that a fallback run still answers, which
# is what makes it silent; the warning pins that cslq noticed, which is the only thing between
# an agent and blaming the latency on us.
log "non-daemon fallback"
fb_pipe="cslq-probe-fallback-$$"
fb_log=$(mktemp)
dotnet run probes/hold-mutex.cs -- "$fb_pipe" 90 > "$fb_log" 2>&1 &
fb_holder=$!

fb_deadline=$(( $(date +%s) + 60 ))
while ! grep -q held "$fb_log" 2>/dev/null; do
  if ! kill -0 "$fb_holder" 2>/dev/null || [ "$(date +%s)" -ge "$fb_deadline" ]; then
    break
  fi
  sleep 1
done

if ! grep -q held "$fb_log" 2>/dev/null; then
  printf 'FAIL  %s (could not hold the daemon startup mutex)\n' "non-daemon-fallback-reported"
  sed 's/^/      | /' "$fb_log"
  fail=$((fail + 1))
else
  # A session of its own. The session key is the root, not the daemon pipe name, so the
  # fixture session the cases left running would answer this out of a daemon that was never
  # contended for -- and the fallback line, which the session prints on its own stderr and
  # sends back over the pipe, would never be produced at all.
  out=$(ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME="$fb_pipe"     CSLQ_SESSION_PIPE_NAME="$SESSION_PREFIX-fallback"     "$CSLQ" ready --root fixture --timeout 300 2>&1)
  rc=$?
  case "$out" in
    *"daemon unreachable"*) ok=$([ "$rc" = 0 ] && echo 1 || echo 0) ;;
    *) ok=0 ;;
  esac
  if [ "$ok" = 1 ]; then
    printf 'PASS  %s\n' "non-daemon-fallback-reported"
    pass=$((pass + 1))
  else
    printf 'FAIL  %s (exit %s, wanted 0 and the fallback warning)\n' "non-daemon-fallback-reported" "$rc"
    printf '%s\n' "$out" | sed 's/^/      | /'
    fail=$((fail + 1))
  fi
fi

kill "$fb_holder" 2>/dev/null
wait "$fb_holder" 2>/dev/null
rm -f "$fb_log"

# The session, which is now what every call gets: the attach is paid once and every later
# call is a round trip to a process that already holds the workspace. A cases.jsonl row cannot
# see a duration, so this is a scripted leg like framework-def-is-labelled-and-does-not-stall.
#
# The first call is what starts the session and pays the load; it is redirected to a file
# rather than captured, because a capturing $(...) hands cslq an inheritable pipe that the
# session it spawns would then hold for its whole life -- the PR #23 bug, one level up.
# 1000 ms is loose on purpose: the query itself is single-digit milliseconds and the rest is
# process start, so a regression here means the session was not reused at all.
#
# A session name of its own, exported for every leg from here down to the workspace
# invalidation one: the cases above have already left a session on the fixture pipe, so the
# first call here would be warm and the cold-then-warm contrast this leg is about would be gone.
log "session"
export CSLQ_SESSION_PIPE_NAME="$SESSION_PREFIX-legs"
sn_first_log=$(mktemp)
sn_log=$(mktemp)
"$CSLQ" hover Greet --root fixture > "$sn_first_log" 2>&1
sn_first=$?
sn_start=$(now_ms)
"$CSLQ" hover Greet --root fixture > "$sn_log" 2>&1
rc=$?
sn_ms=$(( $(now_ms) - sn_start ))
out=$(cat "$sn_log")

ok=1
[ "$sn_first" = 0 ] || ok=0
[ "$rc" = 0 ] || ok=0
[ "$sn_ms" -lt 1000 ] || ok=0
case "$out" in
  *"string Greeter.Greet(string name)"*) ;;
  *) ok=0 ;;
esac
if [ "$ok" = 1 ]; then
  printf 'PASS  %s (%sms)\n' "session-second-call-is-fast" "$sn_ms"
  pass=$((pass + 1))
else
  printf 'FAIL  %s (exit %s after %sms, wanted 0 under 1000ms and the hover signature)\n' \
    "session-second-call-is-fast" "$rc" "$sn_ms"
  # All three, because the interesting failure is a session that started and then went away:
  # the second call then answers correctly, slowly, having started another one, and only the
  # session's own log says why the first is gone.
  printf '      first call (exit %s):\n' "$sn_first"
  sed 's/^/      | /' "$sn_first_log"
  printf '      second call:\n'
  printf '%s\n' "$out" | sed 's/^/      | /'
  printf '      session log:\n'
  sed 's/^/      | /' "$(session_log_path "$CSLQ_SESSION_PIPE_NAME")" 2>/dev/null
  fail=$((fail + 1))
fi
rm -f "$sn_log" "$sn_first_log"

# A session must survive a client that hangs up. The liveness probe used to prove a session
# was up by connecting and dropping the connection, and WaitForPipeAsync did that up to twenty
# times a second -- one of those drops races the session's accept, which then gets
# `IOException: the pipe is being closed`, and rethrowing it took the whole session down: the
# caller fell back, the next call found nothing listening and paid the load again. 4.5 s and
# three processes for one query, on one run in several, which is why this leg exists at all.
#
# The pid count is the real assertion. A session that died and was silently replaced answers
# the query afterwards perfectly well -- slowly, having reloaded the workspace -- so latency
# alone reads as noise. Exactly one `cslq session <version> pid` line in the log means the
# process that answered is the process the leg before this one started.
log "session hangups"
hu_log=$(mktemp)
hu_out=$(mktemp)
dotnet run probes/hangup.cs -- "$CSLQ_SESSION_PIPE_NAME" 50 > "$hu_log" 2>&1
hu_rc=$?
hu_start=$(now_ms)
"$CSLQ" hover Greet --root fixture > "$hu_out" 2>&1
rc=$?
hu_ms=$(( $(now_ms) - hu_start ))
out=$(cat "$hu_out")
hu_pids=$(grep -c '^cslq session .* pid ' "$(session_log_path "$CSLQ_SESSION_PIPE_NAME")" 2>/dev/null)
rm -f "$hu_out"

ok=1
[ "$hu_rc" = 0 ] || ok=0
[ "$rc" = 0 ] || ok=0
[ "$hu_ms" -lt 1000 ] || ok=0
[ "$hu_pids" = 1 ] || ok=0
case "$out" in
  *"string Greeter.Greet(string name)"*) ;;
  *) ok=0 ;;
esac
if [ "$ok" = 1 ]; then
  printf 'PASS  %s (%sms)\n' "session-survives-hangups" "$hu_ms"
  pass=$((pass + 1))
else
  printf 'FAIL  %s (exit %s after %sms, %s session pid lines, wanted 0 under 1000ms and one)\n' \
    "session-survives-hangups" "$rc" "$hu_ms" "$hu_pids"
  printf '      hangups (exit %s):\n' "$hu_rc"
  sed 's/^/      | /' "$hu_log"
  printf '      query:\n'
  printf '%s\n' "$out" | sed 's/^/      | /'
  printf '      session log:\n'
  sed 's/^/      | /' "$(session_log_path "$CSLQ_SESSION_PIPE_NAME")" 2>/dev/null
  fail=$((fail + 1))
fi
rm -f "$hu_log"

# What the session costs correctness, and the reason the staleness record exists. A one-shot
# process re-sent every document's text off disk on every run because its open set started
# empty; a session holds `didOpen` across an edit, and Roslyn owns an open document's text
# rather than re-reading the file -- so without the stat-and-re-send the session answers
# forever from the text it first read: wrong lines, wrong context rows, a renamed symbol
# still found, all at exit 0.
#
# Both directions are asserted. A leg that only checks the edit is visible passes just as
# well if the session threw the whole world away and reloaded it, and a leg that only checks
# the old name is gone passes on a session that broke. Every query goes through the *same*
# session the leg above started -- same pipe, same root -- and into a file rather than a
# $(...) capture, for the inherited-handle reason that leg documents.
#
# Greeter.cs is restored by the EXIT trap, like the generator staleness legs above: an
# interrupt between the rename and the restore must not leave the fixture renamed.
log "session document staleness"
sd_log=$(mktemp)

# Polled rather than asked once, because absence and presence are both eventually consistent
# here: the workspace index is the daemon's and a fresh attach is nobody's to hurry. Without
# the re-send the wanted state never arrives at all, so the deadline is the failure.
await_session_hover() {
  deadline=$(( $(date +%s) + 60 ))
  while :; do
    "$CSLQ" hover "$1" --root fixture > "$sd_log" 2>&1
    rc=$?
    out=$(cat "$sd_log")
    if [ "$2" = present ] && [ "$rc" = 0 ]; then
      case "$out" in *"$3"*) return 0 ;; esac
    fi
    if [ "$2" = absent ] && [ "$rc" = 1 ]; then
      case "$out" in *"no symbol matched"*) return 0 ;; esac
    fi
    if [ "$(date +%s)" -ge "$deadline" ]; then
      printf '%s\n' "$out" | sed 's/^/      | /'
      return 1
    fi
    sleep 1
  done
}

sd_leg() {
  sd_start=$(now_ms)
  if await_session_hover "$2" "$3" "${4:-}"; then
    printf 'PASS  %s (%sms)\n' "$1" "$(( $(now_ms) - sd_start ))"
    pass=$((pass + 1))
  else
    printf 'FAIL  %s (hover %s never became %s)\n' "$1" "$2" "$3"
    fail=$((fail + 1))
  fi
}

sd_leg session-staleness-baseline Greet present "string Greeter.Greet(string name)"
# perl for the reason the rename above uses it: BSD sed has neither -i without a suffix nor \b.
perl -pi -e 's/\bGreet\b/Greeted/g' "$greeter"
sd_leg session-sees-the-edit Greeted present "string Greeter.Greeted(string name)"
sd_leg session-loses-the-old-name Greet absent
cp "$greeter_saved" "$greeter"
sd_leg session-sees-the-restore Greet present "string Greeter.Greet(string name)"
sd_leg session-loses-the-edited-name Greeted absent
rm -f "$sd_log"

# The other half of a living session: the workspace shape itself can change under it. Roslyn's
# file watcher keeps ordinary .cs edits inside a loaded project current; nothing keeps the
# project graph current, so a touched solution has to dispose the attach and take another one.
# Correctness only -- it pays a reload by design, so no latency bound -- plus the session's own
# log line, without which this leg would pass just as well on a session that noticed nothing.
log "session workspace invalidation"
si_log=$(mktemp)
touch fixture/Fixture.slnx
"$CSLQ" hover Greet --root fixture > "$si_log" 2>&1
rc=$?
out=$(cat "$si_log")
rm -f "$si_log"

ok=1
[ "$rc" = 0 ] || ok=0
case "$out" in
  *"string Greeter.Greet(string name)"*) ;;
  *) ok=0 ;;
esac
grep -q "the project graph changed" "$(session_log_path "$CSLQ_SESSION_PIPE_NAME")" 2>/dev/null || ok=0
if [ "$ok" = 1 ]; then
  printf 'PASS  %s\n' "session-reattaches-when-the-solution-changes"
  pass=$((pass + 1))
else
  printf 'FAIL  %s (exit %s, wanted 0, the answer and a re-attach in the session log)\n' \
    "session-reattaches-when-the-solution-changes" "$rc"
  printf '%s\n' "$out" | sed 's/^/      | /'
  printf '      session log:\n'
  sed 's/^/      | /' "$(session_log_path "$CSLQ_SESSION_PIPE_NAME")" 2>/dev/null
  fail=$((fail + 1))
fi

unset CSLQ_SESSION_PIPE_NAME

# A session that cannot be started must cost the caller an answer, never the query. The
# trigger is the startup mutex, held the same way the daemon one is above, and a pipe name
# of its own so the session started by the leg before it is not simply found and used.
#
# Both halves matter, for the reason the daemon fallback's do: exit 0 says the query was
# still answered, and the line says cslq noticed rather than silently taking the slow path.
log "session fallback"
sf_pipe="cslq-probe-session-fb-$$"
sf_log=$(mktemp)
dotnet run probes/hold-mutex.cs -- "$sf_pipe" 90 start > "$sf_log" 2>&1 &
sf_holder=$!

sf_deadline=$(( $(date +%s) + 60 ))
while ! grep -q held "$sf_log" 2>/dev/null; do
  if ! kill -0 "$sf_holder" 2>/dev/null || [ "$(date +%s)" -ge "$sf_deadline" ]; then
    break
  fi
  sleep 1
done

if ! grep -q held "$sf_log" 2>/dev/null; then
  printf 'FAIL  %s (could not hold the session startup mutex)\n' "session-unavailable-falls-back"
  sed 's/^/      | /' "$sf_log"
  fail=$((fail + 1))
else
  sf_out=$(mktemp)
  CSLQ_SESSION_PIPE_NAME="$sf_pipe" "$CSLQ" hover Greet --root fixture --timeout 300 \
    > "$sf_out" 2>&1
  rc=$?
  out=$(cat "$sf_out")
  rm -f "$sf_out"
  ok=$([ "$rc" = 0 ] && echo 1 || echo 0)
  case "$out" in
    *"session unavailable; this run loaded the workspace itself"*) ;;
    *) ok=0 ;;
  esac
  case "$out" in
    *"string Greeter.Greet(string name)"*) ;;
    *) ok=0 ;;
  esac
  if [ "$ok" = 1 ]; then
    printf 'PASS  %s\n' "session-unavailable-falls-back"
    pass=$((pass + 1))
  else
    printf 'FAIL  %s (exit %s, wanted 0, the answer and the fallback warning)\n' \
      "session-unavailable-falls-back" "$rc"
    printf '%s\n' "$out" | sed 's/^/      | /'
    fail=$((fail + 1))
  fi
fi

kill "$sf_holder" 2>/dev/null
wait "$sf_holder" 2>/dev/null
rm -f "$sf_log"

# diag pulls each document once, so the first pull has to be the correct one. Every
# cases.jsonl leg above runs against this suite's shared daemon, which by now has
# App/TypeError.cs open -- a warm document answers correctly whatever the pull count, so
# none of them can catch a regression here. This leg uses a server that has never seen the
# document, the only state where answering before the document binds would show up.
#
# One named file, not the whole fixture: the whole-fixture walk opens Ambient/Stray.cs,
# App/Program.cs and App/Square.cs first, so App/TypeError.cs would be the fourth document
# and the measurement in DESIGN.md -- a cross-project error opened as the *first* document
# in a never-used server -- would have nothing testing it. The rest of the walk is already
# covered warm by deliberate-error-diag-workspace.
cold_log=$(mktemp)
# --no-daemon, not a private pipe name: it gives a dedicated server that has never seen the
# document, which is the state under test, and it exits with the client rather than leaving a
# second daemon holding a warm fixture behind this leg.
#
# --no-session for the same reason, and it is the subject of the leg rather than a workaround:
# a session holds its server for its life, so the no-daemon session the no-daemon-refs case
# left running has a server that loaded the fixture minutes ago. There is no such thing as a
# never-used server inside a session, so this one leg runs without one.
"$CSLQ" diag App/TypeError.cs --root fixture --errors-only --timeout 300 --no-daemon --no-session > "$cold_log" 2>&1
rc=$?
out=$(cat "$cold_log")
rm -f "$cold_log"
case "$out" in
  *"App/TypeError.cs:18:36 error CS0029"*) ok=$([ "$rc" = 0 ] && echo 1 || echo 0) ;;
  *) ok=0 ;;
esac
if [ "$ok" = 1 ]; then
  printf 'PASS  %s
' "cold-server-diag-reports-cross-project-error"
  pass=$((pass + 1))
else
  printf 'FAIL  %s (exit %s, wanted 0 and the cross-project CS0029)
'     "cold-server-diag-reports-cross-project-error" "$rc"
  printf '%s
' "$out" | sed 's/^/      | /'
  fail=$((fail + 1))
fi

# The installed tool. Every leg above runs the binary out of src/Cslq/bin, which still sits
# under this checkout -- ServerArgs.ToolManifestRoot then finds .config/dotnet-tools.json by
# walking up into the repository, which an installed binary has no way to do. This packs,
# installs into a throwaway --tool-path and runs from an unrelated cwd, so the only manifest
# within reach is the one packed into tools/net10.0/any/.
log "packaged tool install"
install_tmp=$(mktemp -d)
version=$(sed -n 's@.*<Version>\(.*\)</Version>.*@\1@p' src/Cslq/Cslq.csproj | head -1)
# --root has to be absolute from a foreign cwd, and MSYS hands a native .NET process a
# Windows path only if it is given one: `pwd -W` on Git Bash, plain `pwd` everywhere else.
fixture_abs=$( cd fixture && { pwd -W 2>/dev/null || pwd; } )

ok=1
dotnet pack src/Cslq/Cslq.csproj -c Release -o "$install_tmp/pkg" --nologo -v q || ok=0
# --source, not --add-source: --add-source only appends the local folder to the feed list, so
# from the first release onward nuget.org offers the same version and the leg could quietly
# test the published package instead of the one just packed. --source replaces every feed,
# and a tool package carries its dependencies, so the local folder is all it needs.
if [ "$ok" = 1 ]; then
  dotnet tool install cslq --version "$version" --tool-path "$install_tmp/bin" \
    --source "$install_tmp/pkg" || ok=0
fi

installed="$install_tmp/bin/cslq"
[ -x "$installed" ] || installed="$installed.exe"
if [ "$ok" = 1 ] && [ -x "$installed" ]; then
  # Redirected to a log rather than captured, so the output survives for printing on failure.
  # --no-session: the subject is an installed binary finding the manifest packed beside it,
  # and a session is answered by whichever binary started it -- the one under src/Cslq/bin,
  # which finds the manifest by walking up into this checkout. A session started by the
  # installed binary instead would hold the throwaway --tool-path open past the EXIT trap.
  ( cd "$install_tmp" && "$installed" ready --root "$fixture_abs" --timeout 300 --no-session ) \
    > "$install_tmp/ready.log" 2>&1
  rc=$?
  out=$(cat "$install_tmp/ready.log")
else
  rc=1
  out="pack or install failed before the binary could run"
fi

case "$out" in
  *ready*) ok=$([ "$rc" = 0 ] && echo 1 || echo 0) ;;
  *) ok=0 ;;
esac
if [ "$ok" = 1 ]; then
  printf 'PASS  %s\n' "installed-tool-resolves-its-own-pin"
  pass=$((pass + 1))
else
  printf 'FAIL  %s (exit %s, wanted 0 and a ready workspace)\n' "installed-tool-resolves-its-own-pin" "$rc"
  printf '%s\n' "$out" | sed 's/^/      | /'
  fail=$((fail + 1))
fi

# The daemon under a harness that captures stdout -- Windows only, and unpinnable from bash:
# `> file` here hands cslq a real file handle and bash waits for exit, not EOF, so the leak
# this leg is about is invisible from the shell running the suite. probes/stdout-capture.cs is
# the harness instead, a .NET process with RedirectStandardOutput, on its own pipe.
#
# Redirected to a file and cat'd, never $(...): the bash capture pipe would leak through the
# app into the daemon exactly the way the bug does, and the leg would hang for the reason it
# is testing.
if pwd -W >/dev/null 2>&1; then
  log "captured stdout"
  sc_log=$(mktemp)
  CSLQ_SESSION_PIPE_NAME="$SESSION_PREFIX-capture"     dotnet run probes/stdout-capture.cs -- "$CSLQ_WIN" > "$sc_log" 2>&1
  rc=$?
  out=$(cat "$sc_log")
  rm -f "$sc_log"
  if [ "$rc" = 0 ]; then
    printf 'PASS  %s\n' "daemon-survives-captured-stdout"
    pass=$((pass + 1))
  else
    printf 'FAIL  %s (exit %s)\n' "daemon-survives-captured-stdout" "$rc"
    printf '%s\n' "$out" | sed 's/^/      | /'
    fail=$((fail + 1))
  fi
fi

# The three first-run failures a user who is not this repository hits, two of which are
# checkable without a server. Both are scripted legs rather than cases.jsonl rows because
# the message alone proves nothing: what each one asserts is that the failure is immediate,
# so the elapsed time has to be checked too.
#
# A root with no solution. --autoLoadProjects does not discover a bare .csproj, so this used
# to load nothing, answer empty and exit 1 only after the whole timeout. The elapsed check is
# the point of the leg: the message alone would pass just as well after 300 s.
log "first-run failures"
ns_tmp=$(mktemp -d)
mkdir -p "$ns_tmp/Lib"
printf '<Project Sdk="Microsoft.NET.Sdk" />' > "$ns_tmp/Lib/Lib.csproj"
printf 'internal class Thing;' > "$ns_tmp/Lib/Thing.cs"
mkdir -p "$ns_tmp/App"
printf '<Project Sdk="Microsoft.NET.Sdk" />' > "$ns_tmp/App/App.csproj"
# MSYS hands a native .NET process a Windows path only if it is given one.
ns_abs=$( cd "$ns_tmp" && { pwd -W 2>/dev/null || pwd; } )

# No CSLQ_SESSION_PIPE_NAME and no --no-session, deliberately: a root with no solution, or
# with two, never gets a session at all -- cslq answers both from a directory listing before
# it would spawn one, which is what keeps the elapsed bound below meaningful. If that guard
# regresses, this leg is where a spawn shows up, as seconds rather than as a message.
ns_start=$(date +%s)
out=$("$CSLQ" ready --root "$ns_abs" --timeout 300 2>&1)
rc=$?
ns_elapsed=$(( $(date +%s) - ns_start ))
rm -rf "$ns_tmp"

ok=1
[ "$rc" = 1 ] || ok=0
[ "$ns_elapsed" -lt 30 ] || ok=0
case "$out" in
  *"cslq: no .sln or .slnx at"*) ;;
  *) ok=0 ;;
esac
case "$out" in
  *"--root must be the directory holding the .sln/.slnx"*) ;;
  *) ok=0 ;;
esac
if [ "$ok" = 1 ]; then
  printf 'PASS  %s\n' "no-solution-root-fails-fast"
  pass=$((pass + 1))
else
  printf 'FAIL  %s (exit %s after %ss, wanted 1 in well under the timeout)\n'     "no-solution-root-fails-fast" "$rc" "$ns_elapsed"
  printf '%s\n' "$out" | sed 's/^/      | /'
  fail=$((fail + 1))
fi

# Two solutions at the root. Same shape and the same reason as the leg above: it is knowable
# from the filesystem, and the elapsed bound is half the assertion -- the `.csproj` scan this
# replaced answered instead, then burned the whole timeout on a project neither solution loads.
ts_tmp=$(mktemp -d)
: > "$ts_tmp/a.sln"
: > "$ts_tmp/b.slnx"
ts_abs=$( cd "$ts_tmp" && { pwd -W 2>/dev/null || pwd; } )

ts_start=$(date +%s)
out=$("$CSLQ" ready --root "$ts_abs" --timeout 300 2>&1)
rc=$?
ts_elapsed=$(( $(date +%s) - ts_start ))

ok=1
[ "$rc" = 1 ] || ok=0
[ "$ts_elapsed" -lt 30 ] || ok=0
for want in "cslq: two solutions at" "a.sln" "b.slnx" "point --root at a directory holding one solution"; do
  case "$out" in
    *"$want"*) ;;
    *) ok=0 ;;
  esac
done
if [ "$ok" = 1 ]; then
  printf 'PASS  %s\n' "two-solutions-root-fails-fast"
  pass=$((pass + 1))
else
  printf 'FAIL  %s (exit %s after %ss, wanted 1 in well under the timeout, naming both files)\n' \
    "two-solutions-root-fails-fast" "$rc" "$ts_elapsed"
  printf '%s\n' "$out" | sed 's/^/      | /'
  fail=$((fail + 1))
fi

# A candidate that can never resolve, on a cold load. `B`'s only type sits inside an
# `#if false` branch, which the candidate regex reads and no compilation ever contains -- a
# shape none of the csproj skip rules can see, because the project is perfectly ordinary. The
# assertion is as much the elapsed time as the message: before the post-notification bound this
# held the full `--timeout`, so a scripted leg rather than a `cases.jsonl` row, which can only
# require a substring and would pass just as well after 150s. `--no-daemon` because the bound
# only applies when `projectInitializationComplete` fires in this process, which on a daemon
# attach it does not. Built from a temp tree rather than a fixture copy: a fixture that never
# becomes ready would have to be excluded from every other leg.
ec_tmp=$(mktemp -d)
mkdir -p "$ec_tmp/A" "$ec_tmp/B"
printf '<Solution>
  <Project Path="A/A.csproj" />
  <Project Path="B/B.csproj" />
</Solution>
'   > "$ec_tmp/Two.slnx"
for proj in A B; do
  printf '<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
'     > "$ec_tmp/$proj/$proj.csproj"
done
printf 'namespace A;

public class Real { }
' > "$ec_tmp/A/Real.cs"
printf '#if false
namespace B;

public class Ghost { }
#endif
' > "$ec_tmp/B/Ghost.cs"
# Deterministic rather than required -- see the fixture restore at the top.
dotnet restore "$ec_tmp/Two.slnx" --nologo -v q > /dev/null 2>&1
ec_abs=$( cd "$ec_tmp" && { pwd -W 2>/dev/null || pwd; } )

ec_pipe="$SESSION_PREFIX-exhausted"
ec_start=$(date +%s)
out=$(CSLQ_SESSION_PIPE_NAME="$ec_pipe" "$CSLQ" ready --root "$ec_abs" --no-daemon --timeout 150 2>&1)
rc=$?
ec_elapsed=$(( $(date +%s) - ec_start ))
# Through the session like every other call, so the bound covers the session load too -- and
# stopped by name before the tree goes, or it would hold a dedicated server for a root that no
# longer exists until the keepalive. `session stop` is the user-facing lever for exactly this.
CSLQ_SESSION_PIPE_NAME="$ec_pipe" "$CSLQ" session stop --root "$ec_abs" --no-daemon > /dev/null 2>&1
rm -rf "$ec_tmp"
ec_tmp=""

ok=1
[ "$rc" = 1 ] || ok=0
[ "$ec_elapsed" -lt 90 ] || ok=0
for want in "did not become ready" "Ghost" "for project B" "projectInitializationComplete fired"; do
  case "$out" in
    *"$want"*) ;;
    *) ok=0 ;;
  esac
done
if [ "$ok" = 1 ]; then
  printf 'PASS  %s (%ss)\n' "exhausted-candidate-fails-after-load" "$ec_elapsed"
  pass=$((pass + 1))
else
  printf 'FAIL  %s (exit %s after %ss, wanted 1 under 90s naming B and the notification)\n' \
    "exhausted-candidate-fails-after-load" "$rc" "$ec_elapsed"
  printf '%s\n' "$out" | sed 's/^/      | /'
  fail=$((fail + 1))
fi

# A design-time build that cannot run at all: a `global.json` pinning an SDK nobody has
# installed. Testers measured 181.7 s and a message naming neither global.json, the SDK nor
# MSBuild -- the notification fires, the solution loads, and every project answers empty,
# because MSBuild never produced a compilation. That combination is the tell-tale, and the
# cause is one `dotnet --version` away with the working directory set to the root.
#
# A scripted leg for the same two reasons the legs above are: the state has to be staged and
# torn down, and the elapsed bound is half the assertion -- the whole point is that this no
# longer costs the full `--timeout`. `--no-daemon`, so the broken root cannot be answered out
# of a daemon another leg has already loaded something into.
gj_tmp=$(mktemp -d)
mkdir -p "$gj_tmp/Lib"
printf '{ "sdk": { "version": "9.9.900", "rollForward": "disable" } }
' > "$gj_tmp/global.json"
printf '<Solution>
  <Project Path="Lib/Lib.csproj" />
</Solution>
' > "$gj_tmp/One.slnx"
printf '<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
' > "$gj_tmp/Lib/Lib.csproj"
printf 'namespace Lib;

public class LibType { }
' > "$gj_tmp/Lib/LibType.cs"
gj_abs=$( cd "$gj_tmp" && { pwd -W 2>/dev/null || pwd; } )

gj_pipe="$SESSION_PREFIX-globaljson"
gj_start=$(date +%s)
out=$(CSLQ_SESSION_PIPE_NAME="$gj_pipe" "$CSLQ" ready --root "$gj_abs" --no-daemon --timeout 150 2>&1)
rc=$?
gj_elapsed=$(( $(date +%s) - gj_start ))
CSLQ_SESSION_PIPE_NAME="$gj_pipe" "$CSLQ" session stop --root "$gj_abs" --no-daemon > /dev/null 2>&1
rm -rf "$gj_tmp"
gj_tmp=""

ok=1
[ "$rc" = 1 ] || ok=0
[ "$gj_elapsed" -lt 90 ] || ok=0
for want in "every probed project answered empty" "cause: the .NET SDK cannot run in this root" "9.9.900" "global.json"; do
  case "$out" in
    *"$want"*) ;;
    *) ok=0 ;;
  esac
done
if [ "$ok" = 1 ]; then
  printf 'PASS  %s (%ss)\n' "failed-design-time-build-names-the-sdk" "$gj_elapsed"
  pass=$((pass + 1))
else
  printf 'FAIL  %s (exit %s after %ss, wanted 1 under 90s naming the SDK and global.json)\n' \
    "failed-design-time-build-names-the-sdk" "$rc" "$gj_elapsed"
  printf '%s\n' "$out" | sed 's/^/      | /'
  fail=$((fail + 1))
fi

# `dotnet` off PATH. The binary is an apphost and finds its own runtime through DOTNET_ROOT,
# so exporting that to the SDK's real directory -- symlinks resolved, which is what /usr/bin
# on a runner needs -- leaves the launch of the *server* as the only thing PATH is still
# needed for. Emptying PATH rather than filtering it out of dotnet directories: on a runner
# `dotnet` lives in /usr/bin, so there is no dotnet-shaped entry to drop.
dotnet_real=$(command -v dotnet)
dotnet_real=$(readlink -f "$dotnet_real" 2>/dev/null || printf '%s' "$dotnet_real")
dotnet_root=$( cd "$(dirname "$dotnet_real")" && { pwd -W 2>/dev/null || pwd; } )

# A session of its own, and the failure has to travel out of it: the session is spawned from
# Environment.ProcessPath and inherits the emptied PATH, so it is the session that cannot
# launch the server and the message comes back over the pipe. The fixture session the cases
# left running would answer this successfully, which is why the name is not pipe_for's.
op_pipe="$SESSION_PREFIX-nopath"
out=$(DOTNET_ROOT="$dotnet_root" PATH="" CSLQ_SESSION_PIPE_NAME="$op_pipe" "$CSLQ" ready --root fixture --timeout 300 2>&1)
rc=$?
CSLQ_SESSION_PIPE_NAME="$op_pipe" "$CSLQ" session stop --root fixture > /dev/null 2>&1
ok=$([ "$rc" = 1 ] && echo 1 || echo 0)
case "$out" in
  *"cslq: could not start the language server"*) ;;
  *) ok=0 ;;
esac
case "$out" in
  *".NET 10 SDK must be installed and on PATH"*) ;;
  *) ok=0 ;;
esac
case "$out" in
  *"   at "*|*"   em "*) ok=0 ;;
esac
if [ "$ok" = 1 ]; then
  printf 'PASS  %s\n' "dotnet-off-path-reports"
  pass=$((pass + 1))
else
  printf 'FAIL  %s (exit %s, wanted 1 and a one-line cslq: message)\n' "dotnet-off-path-reports" "$rc"
  printf '%s\n' "$out" | sed 's/^/      | /'
  fail=$((fail + 1))
fi

# `cslq restore`, the pre-warm, against a tool that reads as unrestored. Moving the resolver
# cache entry aside is what produces that state cheaply: the package itself stays in
# ~/.nuget/packages, so the restore re-resolves the pin in about a second rather than paying the
# ~300 MB download a genuine first run pays. A scripted leg rather than a cases.jsonl row for the
# same reasons `no-solution-root-fails-fast` is one -- the state under test has to be set up and
# put back, and the elapsed bound is half the assertion. Placed here, and not earlier, because
# the cache is shared with every other leg: the daemon is long since launched by now and nothing
# below has to resolve the tool again.
log "restore"
cache_entry="${DOTNET_CLI_HOME:-$HOME}/.dotnet/toolResolverCache/1/roslyn-language-server"
[ -e "$cache_entry" ] && { cache_saved="$cache_entry.probe-$$"; mv "$cache_entry" "$cache_saved"; }

rs_log=$(mktemp)
rs_start=$(date +%s)
# Redirected to a log rather than captured, so the output survives for printing on failure.
"$CSLQ" restore > "$rs_log" 2>&1
rc=$?
rs_elapsed=$(( $(date +%s) - rs_start ))
out=$(cat "$rs_log")
rm -f "$rs_log"

# Asserted before the cleanup puts the saved entry back, or there would be nothing left to
# distinguish a restore that rebuilt the cache from one that only printed the message.
ok=1
[ -e "$cache_entry" ] || ok=0

if [ -n "$cache_saved" ]; then
  rm -rf "$cache_entry"
  mv "$cache_saved" "$cache_entry"
  cache_saved=""
fi

[ "$rc" = 0 ] || ok=0
# The manifest `restore` names is the one above the running binary, which for this checkout is
# the repository root. A pre-warm that restored somewhere else would still exit 0.
case "$out" in
  *"restored the pinned language server in "*) ;;
  *) ok=0 ;;
esac
# Separators folded rather than matched: .NET prints a Windows path with backslashes and
# `pwd -W` hands back the same directory with forward slashes.
case "$(printf '%s' "$out" | tr "\\\\" /)" in
  *"$(printf '%s' "$root_abs" | tr "\\\\" /)"*) ;;
  *) ok=0 ;;
esac
# And it must not have gone to the network: only the resolver cache was missing, the payload
# behind the pin is already in ~/.nuget/packages. A restore that downloads takes minutes.
[ "$rs_elapsed" -lt 60 ] || ok=0
if [ "$ok" = 1 ]; then
  printf 'PASS  %s (%ss)\n' "restore-rebuilds-the-tool-resolver-cache" "$rs_elapsed"
  pass=$((pass + 1))
else
  printf 'FAIL  %s (exit %s after %ss, wanted 0 under 60s rebuilding the resolver cache and naming %s)\n' \
    "restore-rebuilds-the-tool-resolver-cache" "$rc" "$rs_elapsed" "$root_abs"
  printf '%s\n' "$out" | sed 's/^/      | /'
  fail=$((fail + 1))
fi

printf '\n%s passed, %s failed\n' "$pass" "$fail"
[ "$fail" -eq 0 ]
