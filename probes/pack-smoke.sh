#!/usr/bin/env bash
#
# The packaging gate, and it is not a leg of probes/run.sh: it costs a Native AOT publish
# (~70 s) and an install, which the local gate should not pay on every run. It proves on this
# host the one thing no build can -- that the four packages a release would push actually
# install here and that the installed binary can still resolve the server pin, which under RID
# packaging depends on a PackagePath that no compilation ever reads.
#
# Run it AFTER probes/run.sh: the `ready` check below needs fixture/ restored and fixture/Gen
# built, which run.sh is what does.
#
# It takes an optional output directory: given one it packs there and leaves it, which is how
# release.yml gets the nupkgs it uploads. With no argument it packs into a temp directory and
# deletes it, so a local run leaves nothing behind.
#
# One host, one RID: Native AOT cannot cross-compile across operating systems, so each of the
# three advertised native RIDs is proved by the runner that can build it. probe.yml's matrix is
# already exactly those three.
set -uo pipefail

cd "$(dirname "$0")/.."

# A packed nupkg is a zip and the manifest's placement inside it is the whole point of the
# assertions below, so a host without unzip cannot run this rather than pass it.
if ! command -v unzip >/dev/null 2>&1; then
  echo "pack-smoke: unzip is required to read a nupkg" >&2
  exit 1
fi

case "$(uname -s)" in
  Linux*)  rid=linux-x64 ;;
  Darwin*) rid=osx-arm64 ;;
  MINGW*|MSYS*|CYGWIN*)
    rid=win-x64
    # An AOT publish from Git Bash fails at ILCompiler's link step with MSB3073/123 even with
    # MSVC installed and found: vswhere.exe is not on Git Bash's PATH, and this is the shell
    # the Windows runner gives a bash step.
    PATH="/c/Program Files (x86)/Microsoft Visual Studio/Installer:$PATH"
    export PATH
    ;;
  *) echo "pack-smoke: unrecognised host $(uname -s)" >&2; exit 1 ;;
esac

version=$(sed -n 's@.*<Version>\(.*\)</Version>.*@\1@p' src/Cslq/Cslq.csproj | head -1)
fixture_abs=$( cd fixture && { pwd -W 2>/dev/null || pwd; } )

# A daemon and a session of this run's own, for the reasons run.sh scopes its own: an ambient
# daemon holds a workspace this script never built, and anything started here must not outlive
# it. The install below is thrown away at EXIT, so nothing may still be running out of it.
export ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME="cslq-packsmoke-$$"
export ROSLYN_LANGUAGE_SERVER_DAEMON_KEEPALIVE=20
export CSLQ_SESSION_PIPE_NAME="cslq-packsmoke-session-$$"
export CSLQ_SESSION_KEEPALIVE=20

out="${1:-}"
out_is_ours=1
if [ -n "$out" ]; then
  mkdir -p "$out" || exit 1
  out_is_ours=0
else
  out=$(mktemp -d)
fi
bin=$(mktemp -d)

session_dir() {
  if command -v cygpath >/dev/null 2>&1 && [ -n "${TEMP:-}" ]; then
    cygpath -u "$TEMP"
  else
    printf '%s' "${TMPDIR:-/tmp}"
  fi
}

cleanup() {
  session_log="$(session_dir)/cslq-session-$CSLQ_SESSION_PIPE_NAME.log"
  if [ -f "$session_log" ]; then
    session_pid=$(sed -n 's/^cslq session [^ ]* pid \([0-9]*\) .*/\1/p' "$session_log" | tail -1)
    if [ -n "$session_pid" ]; then
      if command -v taskkill >/dev/null 2>&1; then
        taskkill //F //PID "$session_pid" >/dev/null 2>&1
      else
        kill "$session_pid" 2>/dev/null
      fi
    fi
  fi
  [ "$out_is_ours" = 1 ] && rm -rf "$out"
  rm -rf "$bin" 2>/dev/null
  return 0
}
trap cleanup EXIT

log() { printf '\n==> %s\n' "$*"; }
pass=0
fail=0
report() {
  if [ "$1" = 1 ]; then
    printf 'PASS  %s\n' "$2"; pass=$((pass + 1))
  else
    printf 'FAIL  %s\n' "$2"; fail=$((fail + 1))
  fi
}

# The pointer package first, then this host's RID, then the framework-dependent fallback.
# The pointer carries no binary of its own -- it lists the RIDs and NuGet picks the
# sub-package at restore -- and `-r any -p:PublishAot=false` is what makes the fallback
# CoreCLR rather than a native build for a RID that does not exist.
log "pack pointer, $rid and any"
packed=1
dotnet pack src/Cslq/Cslq.csproj -c Release -o "$out" --nologo -v q || packed=0
dotnet pack src/Cslq/Cslq.csproj -c Release -r "$rid" -o "$out" --nologo -v q || packed=0
dotnet pack src/Cslq/Cslq.csproj -c Release -r any -p:PublishAot=false -o "$out" --nologo -v q || packed=0
report "$packed" "three packages pack"
[ "$packed" = 1 ] || { printf '\n%s passed, %s failed\n' "$pass" "$fail"; exit 1; }

ls -1 "$out"

# The manifest has to sit beside the binary, and where that is moves with the package:
# tools/any/<rid>/ for a Native AOT pack, which is self-contained, and tools/net10.0/any/ for
# the framework-dependent fallback. ServerArgs.ToolManifestRoot walks up from
# AppContext.BaseDirectory, so a manifest one directory sideways is a manifest the installed
# tool can never find -- and no build, publish or pack can see the difference.
#
# Asserted against DotnetToolSettings.xml's own directory rather than against a path written
# down here as well: that file is where the runner and the entry point are declared, so it is
# the binary's directory by definition, whatever the SDK decides to call the two segments.
beside_the_binary() {
  entries=$(unzip -Z1 "$out/$1" 2>/dev/null)
  settings=$(printf '%s\n' "$entries" | grep -m1 '/DotnetToolSettings\.xml$')
  [ -n "$settings" ] || return 1
  printf '%s\n' "$entries" | grep -qx "${settings%DotnetToolSettings.xml}.config/dotnet-tools.json"
}
report "$(beside_the_binary "cslq.$rid.$version.nupkg" && echo 1 || echo 0)" \
  "the $rid package carries the manifest beside its binary"
report "$(beside_the_binary "cslq.any.$version.nupkg" && echo 1 || echo 0)" \
  "the any package carries the manifest beside its binary"

# ~/.dotnet/toolResolverCache/1/<tool> is keyed by name and version and holds an absolute
# PathToExecutable, so an install left by an earlier run answers here and the run below
# exercises a binary this one never produced -- at exit 0, which is how four scenarios passed
# meaninglessly while this was being settled.
rm -f "$HOME/.dotnet/toolResolverCache/1/cslq"

# --source, not --add-source, for the reason run.sh's install leg gives: --add-source only
# appends the local folder, so from the first release onward nuget.org offers the same version
# and this could quietly install the published package instead of the one just packed.
log "install from the local feed"
installed=1
dotnet tool install --tool-path "$bin" --source "$out" cslq --version "$version" || installed=0
report "$installed" "the pointer package installs"
[ "$installed" = 1 ] || { printf '\n%s passed, %s failed\n' "$pass" "$fail"; exit 1; }

# What --tool-path lays down depends on the runner the package declares. A RID package says
# Runner="executable", and the shim for one on Windows is a .cmd rather than the apphost .exe
# a framework-dependent tool gets, so all three spellings have to be looked for.
cslq=""
for candidate in "$bin/cslq" "$bin/cslq.exe" "$bin/cslq.cmd"; do
  [ -f "$candidate" ] && { cslq="$candidate"; break; }
done
report "$([ -n "$cslq" ] && echo 1 || echo 0)" "the install leaves a runnable shim"
[ -n "$cslq" ] || { printf '\n%s passed, %s failed\n' "$pass" "$fail"; exit 1; }

log "$cslq --version"
ver_out=$("$cslq" --version 2>&1)
ver_rc=$?
printf '%s\n' "$ver_out"
report "$([ "$ver_rc" = 0 ] && [ "$ver_out" = "$version" ] && echo 1 || echo 0)" \
  "the installed binary prints $version"

# The one check that needs a workspace: `ready` launches the language server, which the
# installed binary can only do if it resolved the pin out of the .config/ packed beside it.
# --no-session for the reason run.sh's install leg gives -- a session started by this binary
# would hold the throwaway --tool-path open past the EXIT trap that deletes it.
log "$cslq ready --root fixture"
ready_out=$( cd "$out" && "$cslq" ready --root "$fixture_abs" --timeout 300 --no-session 2>&1 )
ready_rc=$?
printf '%s\n' "$ready_out"
report "$([ "$ready_rc" = 0 ] && echo 1 || echo 0)" "the installed binary resolves the pin and reaches ready"

printf '\n%s passed, %s failed\n' "$pass" "$fail"
[ "$fail" = 0 ]
