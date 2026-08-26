#!/usr/bin/env bash
# §1b — what is a named Mutex actually scoped to?
# Four scenarios, same code, same name. Run: ./03-mutex-scope.sh
set -u
cd "$(dirname "$0")"
OUT="${TMPDIR:-/tmp}/mutexb-$$"
HOLD=25

cleanup() { pkill -f '03-mutex-a' 2>/dev/null; rm -rf "$OUT"; }
trap cleanup EXIT

if [[ " ${*:-1 2 3 4} " == *" 3 "* || " ${*:-1 2 3 4} " == *" 4 "* ]]; then
  echo "publishing contender for the container run..."
  dotnet publish 03-mutex-b.cs -o "$OUT" >/dev/null 2>&1 || { echo "publish failed"; exit 1; }
fi

run_case() {
  local title="$1" name="$2" mode="$3"
  echo
  echo "=============================================================="
  echo " $title"
  echo "   name: $name"
  echo "=============================================================="
  MUTEX_NAME="$name" setsid dotnet run 03-mutex-a.cs -- $HOLD >/tmp/mx-a.log 2>&1 </dev/null &
  sleep 9
  grep -E '^\[A\] (pid|ACQUIRED)' /tmp/mx-a.log

  case "$mode" in
    session)
      MUTEX_NAME="$name" setsid dotnet run 03-mutex-b.cs >/tmp/mx-b.log 2>&1 </dev/null &
      sleep 6; grep '^\[B\]' /tmp/mx-b.log ;;
    docker)
      docker run --rm -v "$OUT:/app" -v /tmp:/tmp -e "MUTEX_NAME=$name" \
        mcr.microsoft.com/dotnet/runtime:10.0 dotnet /app/03-mutex-b.dll 2>&1 | grep '^\[B\]' ;;
  esac
  pkill -f '03-mutex-a' 2>/dev/null
  sleep 1
}

# Pass scenario numbers to run a subset:  ./03-mutex-scope.sh 1 2
WANT="${*:-1 2 3 4}"
want() { [[ " $WANT " == *" $1 "* ]]; }

want 1 && run_case "1. Different POSIX sessions, UNPREFIXED name" 'OneTrueLock'        session
want 2 && run_case "2. Different POSIX sessions, Global\\ prefix"  'Global\OneTrueLock' session
want 3 && run_case "3. Container sharing /tmp, UNPREFIXED name"   'OneTrueLock'        docker
want 4 && run_case "4. Container sharing /tmp, Global\\ prefix"    'Global\OneTrueLock' docker

cat <<'EOF'

==============================================================
 What just happened
==============================================================

  scenario                              unprefixed    Global\
  ------------------------------------  -----------   ---------
  different POSIX session               ACQUIRED      BLOCKED
  container sharing /tmp                ACQUIRED      BLOCKED

On Unix, .NET backs named mutexes with FILES:

  unprefixed  ->  /tmp/.dotnet/shm/session<sid>/<name>
  Global\     ->  /tmp/.dotnet/shm/global/<hash>

So an unprefixed named Mutex is scoped to the POSIX SESSION, not the
machine. Two systemd units, two SSH logins, a terminal vs a service --
different sessions, no mutual exclusion, no error.

Every "only one instance of this app may run" guard built on an
unprefixed named Mutex is broken in exactly this way, silently.

And note scenario 3: sharing /tmp is NOT enough on its own. The
container's PID 1 is session 1, so it looks in session1/ and finds
nothing. It takes BOTH a shared /tmp and a Global\ name to contend
across the container boundary.

On WINDOWS the same trap exists by a different mechanism: named mutexes
are kernel objects, unprefixed means Local\ = per Terminal Services
session, and a service runs in session 0 while a desktop app runs in
session 1+. They do not contend.

And across the WSL boundary nothing is shared at all -- a Windows process
and a WSL process never contend on the same name, Global\ included. If you
develop in WSL and deploy to Windows, "it worked on my machine" tells you
nothing about the locking behaviour of the deployed thing.

(This script tests the Linux side. The Windows rows were verified
separately via WSL interop -- see ../NOTES.md.)

The lock's scope is the scope of the thing implementing it.
EOF
