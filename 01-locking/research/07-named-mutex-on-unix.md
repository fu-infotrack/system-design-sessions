# Q7 — Named `Mutex` on Unix / .NET

> ### ⚠️ Correction from re-running this (2026-08-26)
>
> This file states that a container **blocks** when given `-v /tmp:/tmp`.
> That does **not** reproduce. Re-tested against `demos/03-mutex-scope.sh`:
>
> | scenario | unprefixed | `Global\` |
> |---|---|---|
> | different POSIX session | ACQUIRED (no contention) | **BLOCKED** |
> | container sharing `/tmp` | ACQUIRED (no contention) | **BLOCKED** |
>
> Sharing `/tmp` is **not sufficient on its own**. A container's PID 1 is
> session 1, so it looks in `session1/` and finds nothing there. Contending
> across the container boundary needs **both** a shared `/tmp` **and** a
> `Global\`-prefixed name.
>
> The file's core finding — that unprefixed names are POSIX-session scoped —
> is confirmed, and is the reason the `/tmp` mount alone does nothing.
>
> ### Windows, now verified rather than documented (2026-08-26)
>
> §9's Windows material was documentation-only. Tested against a real Windows
> SDK 10.0.400 via WSL interop:
>
> | Holder | Contender | Name | Result |
> |---|---|---|---|
> | Windows | Windows, same session | unprefixed | **BLOCKED** |
> | Windows | WSL | unprefixed | acquired — no contention |
> | Windows | WSL | `Global\` | acquired — no contention |
>
> Windows-to-Windows contention works as documented. **WSL and Windows never
> share a named mutex, `Global\` included** — different implementations, and
> WSL2 is a separate VM.
>
> Still documentation-only, not tested: Windows contention *across* Terminal
> Services sessions (needs a service or a second RDP session), and
> `AbandonedMutexException` on Windows.


## Summary

On Unix, .NET implements named mutexes itself, in userspace, backed by a **file under `/tmp`**:
`/tmp/.dotnet/shm/global/<name>` for `Global\`-prefixed names and
`/tmp/.dotnet/shm/session<sid>/<name>` otherwise. The file is `mmap`ped `MAP_SHARED` and — on Linux —
holds a **`pthread` process-shared, robust, recursive mutex**; on platforms without usable robust
pthread mutexes (macOS, FreeBSD, OpenBSD, Haiku) it falls back to **`flock`** on a second empty file
under `/tmp/.dotnet/lockfiles/...`. Because a container has its own mount namespace, it has its own
`/tmp`, therefore its own backing file, therefore **no contention with the host at all** — I verified
this both ways with Docker. Two further gotchas I verified: the default (unprefixed) scope on Unix is
the **POSIX session** from `getsid(2)`, not the machine; and `AbandonedMutexException` is **silently
lost** if the crashing process was the only one holding the mutex open.

> Verification environment: .NET SDK 10.0.302 / runtime 10.0.10, Linux WSL2 kernel 6.18, glibc, uid 1000;
> Docker with `mcr.microsoft.com/dotnet/runtime:10.0`. Source citations are pinned to `release/9.0`
> (which matches `release/10.0`); see §7 for the .NET 11 rewrite.

---

## Findings

### 1. Where the implementation lives

For **.NET 9 and .NET 10** it is C++ in the CoreCLR PAL:

- `src/coreclr/pal/src/synchobj/mutex.cpp` — `NamedMutexProcessData`, `NamedMutexSharedData`, `MutexHelpers`
- `src/coreclr/pal/src/include/pal/mutex.hpp` — the design comment quoted below
- `src/coreclr/pal/src/sharedmemory/sharedmemory.cpp` + `src/coreclr/pal/src/include/pal/sharedmemory.h` — the file layout, naming and lifetime machinery

Sources:
- <https://github.com/dotnet/runtime/blob/release/9.0/src/coreclr/pal/src/synchobj/mutex.cpp>
- <https://github.com/dotnet/runtime/blob/release/9.0/src/coreclr/pal/src/include/pal/mutex.hpp>
- <https://github.com/dotnet/runtime/blob/release/9.0/src/coreclr/pal/src/sharedmemory/sharedmemory.cpp>
- <https://github.com/dotnet/runtime/blob/release/9.0/src/coreclr/pal/src/include/pal/sharedmemory.h>

### 2. The design, straight from the source comment

`pal/mutex.hpp` carries an unusually candid design block. Verbatim excerpt:

```
Design

- On systems that support pthread process-shared robust recursive mutexes, they will be used
- On other systems, file locks are used. File locks unfortunately don't have a timeout in the blocking
  wait call, and I didn't find any other sync object with a timed wait with the necessary properties,
  so polling is done for timed waits.

Shared memory files
- Session-scoped mutexes (name not prefixed, or prefixed with Local) go in /tmp/.dotnet/shm/session<sessionId>/<mutexName>
- Globally-scoped mutexes (name prefixed with Global) go in /tmp/.dotnet/shm/global/<mutexName>
- Contains shared state, and is mmap'ped into the process, ...
- Creation and deletion is synchronized using an exclusive file lock on the shm directory
- Any process using the shared memory file holds a shared file lock on the shared memory file
- Upon creation, if the shared memory file already exists, an exclusive file lock is attempted on it, to
  see if the file data is valid. If no other processes have the mutex open, the file is reinitialized.
- Upon releasing the last reference to a mutex in a process, it will try to get an exclusive lock on the
  shared memory file to see if any other processes have the mutex opened. If not, the file is deleted...
- ... Depending on how the process terminated, the file may still be left over in the tmp directory,
  I haven't found anything that can be done about that.

Lock files when using file locks:
- In addition to the shared memory file, we need another file for the actual synchronization file lock,
  since a file lock on the shared memory file is used for lifetime purposes.
- These files go in /tmp/.dotnet/lockfiles/session<sessionId>|global/<mutexName>
- The file is empty, and is only used for file locks

Abandon detection
- When a lock is acquired, the process data is added to a linked list on the owning thread
- When a thread exits, the list is walked, each mutex is flagged as abandoned and released
- For detecting process abruptly terminating, pthread robust mutexes give us that. When using file locks,
  the file lock is automatically released by the system. Upon acquiring a lock, the lock owner info in
  the shared memory is checked to see if the mutex was abandoned.
```

Source: <https://github.com/dotnet/runtime/blob/release/9.0/src/coreclr/pal/src/include/pal/mutex.hpp>

### 3. Which primitive, on which platform

The pthread path is selected at compile time:

```cpp
#if HAVE_FULLY_FEATURED_PTHREAD_MUTEXES && \
    HAVE_FUNCTIONAL_PTHREAD_ROBUST_MUTEXES && \
    !(defined(__FreeBSD__) || defined(TARGET_OSX))
    #define NAMED_MUTEX_USE_PTHREAD_MUTEX 1
#else
    #define NAMED_MUTEX_USE_PTHREAD_MUTEX 0
#endif
```

and when taken, the mutex placed in the shared mapping is configured exactly as advertised:

```cpp
pthread_mutexattr_setpshared(&mutexAttributes, PTHREAD_PROCESS_SHARED);
pthread_mutexattr_setrobust(&mutexAttributes, PTHREAD_MUTEX_ROBUST);
pthread_mutexattr_settype(&mutexAttributes, PTHREAD_MUTEX_RECURSIVE);
pthread_mutex_init(mutex, &mutexAttributes);
```

with `EOWNERDEAD` handled by `pthread_mutex_consistent()` and reported as
`MutexTryAcquireLockResult::AcquiredLockButMutexWasAbandoned`.

Sources: <https://github.com/dotnet/runtime/blob/release/9.0/src/coreclr/pal/src/include/pal/mutex.hpp>,
<https://github.com/dotnet/runtime/blob/release/9.0/src/coreclr/pal/src/synchobj/mutex.cpp>

The exclusions are documented inline:

> - On FreeBSD, pthread process-shared robust mutexes cannot be placed in shared memory mapped
>   independently by the processes involved. See https://github.com/dotnet/runtime/issues/10519.
> - On OSX, pthread robust mutexes were/are not available at the time of this writing.

The `main`-branch (.NET 11) managed port adds OpenBSD and Haiku to the exclusion list:

```csharp
private static bool UsePThreadMutexes => !OperatingSystem.IsApplePlatform() && !OperatingSystem.IsFreeBSD()
                                       && !OperatingSystem.IsOpenBSD() && !OperatingSystem.IsHaiku();
```

Source: <https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Threading/NamedMutex.Unix.cs>

The `flock` fallback is `flock(fd, LOCK_EX | LOCK_NB)` / `LOCK_SH` / `LOCK_UN` in `sharedmemory.cpp`.
Note the design comment's admission that `flock` has no timed wait, so timed waits on those platforms
**poll**, with `PollLoopMaximumSleepMilliseconds = 100`.

**Verified on Linux/glibc.** I created a `Local`- and a `Global`-scoped named mutex and inspected the
filesystem while both were held. Only `shm` files appeared — **no `lockfiles` directory** — confirming
the pthread path is live on Linux:

```
drwxrwxrwx fu:fu  /tmp/.dotnet
drwxrwxrwx fu:fu  /tmp/.dotnet/shm
drwxrwxrwx fu:fu  /tmp/.dotnet/shm/global
drwxrwxrwx fu:fu  /tmp/.dotnet/shm/session3540691
-rw-rw-rw- fu:fu  /tmp/.dotnet/shm/global/KnowledgeSharingGlobalDemo        (4096 bytes)
-rw-rw-rw- fu:fu  /tmp/.dotnet/shm/session3540691/KnowledgeSharingLocalDemo (4096 bytes)
```

### 4. Where `/tmp` comes from

The header documents the layout against a variable:

```
// The folder used for storing shared memory files and their lock files is defined in
// the gSharedFilesPath global variable. The value of the variable depends on which
// OS is being used, and if the application is running in a sandbox in Mac.
// - Global shared memory files go in:     {gSharedFilesPath}/.dotnet/shm/global/<fileName>
// - Session-scoped shared memory files go in: {gSharedFilesPath}/.dotnet/shm/session<sessionId>/<fileName>
// - Lock files associated with global shared memory files go in: {gSharedFilesPath}/.dotnet/lockfiles/global/<fileName>
// - Lock files ... session-scoped ...:    {gSharedFilesPath}/.dotnet/lockfiles/session<sessionId>/<fileName>
```

```c
#define SHARED_MEMORY_RUNTIME_TEMP_DIRECTORY_NAME  ".dotnet"
#define SHARED_MEMORY_SHARED_MEMORY_DIRECTORY_NAME ".dotnet/shm"
#define SHARED_MEMORY_LOCK_FILES_DIRECTORY_NAME    ".dotnet/lockfiles"
#define SHARED_MEMORY_GLOBAL_DIRECTORY_NAME        "global"
#define SHARED_MEMORY_SESSION_DIRECTORY_NAME_PREFIX "session"
```

Source: <https://github.com/dotnet/runtime/blob/release/9.0/src/coreclr/pal/src/include/pal/sharedmemory.h>

`gSharedFilesPath` falls back to `TEMP_DIRECTORY_PATH`:

```cpp
// If we are here, then we are not in sandbox mode, resort to TEMP_DIRECTORY_PATH as shared files path
return gSharedFilesPath->Set(TEMP_DIRECTORY_PATH);
```

Source: <https://github.com/dotnet/runtime/blob/release/9.0/src/coreclr/pal/src/init/pal.cpp>

and `TEMP_DIRECTORY_PATH` is a hard-coded literal:

```c
#ifndef __ANDROID__
#define TEMP_DIRECTORY_PATH "/tmp/"
#else
// On Android, "/tmp/" doesn't exist; temporary files should go to /data/local/tmp/
#define TEMP_DIRECTORY_PATH "/data/local/tmp/"
#endif
```

Source: <https://github.com/dotnet/runtime/blob/release/9.0/src/coreclr/pal/src/include/pal/palinternal.h>

**It is not `TMPDIR`, and it is not an XDG path.** It is literally `/tmp/`. The only deviation is macOS
sandboxed apps, where an application-group container path is used instead (`GetApplicationContainerFolder`).

### 5. Why a container does not contend with the host — confirmed, not assumed

The mechanism is: **the named mutex *is* the file.** There is no kernel-global namespace involved
(unlike Windows object names, or POSIX named semaphores). A container gets its own mount namespace,
hence its own `/tmp`, hence a different file at the same path — so the two processes initialise two
completely independent pthread mutexes and never see each other.

**Test 1 — host holds `Global\DockerDemo`, container tries the same name, no shared `/tmp`:**

```
container: pid=1 name=Global\DockerDemo
container: ACQUIRED
host:      pid=3545043 name=Global\DockerDemo
host:      ACQUIRED
```

Both acquired. Mutual exclusion did not happen. And a fresh container's `/tmp` is empty:

```
$ docker run --rm mcr.microsoft.com/dotnet/runtime:10.0 ls -la /tmp
total 8
drwxrwxrwt 1 root root 4096 ...
drwxr-xr-x 1 root root 4096 ...
```

**Test 2 — identical, but with `-v /tmp:/tmp`:**

```
container: pid=1 name=Global\DockerDemo2
container: BLOCKED-timeout
host:      pid=3546189 name=Global\DockerDemo2
host:      ACQUIRED
backing file: -rw-rw-rw- fu:fu /tmp/.dotnet/shm/global/DockerDemo2
```

The container blocked. So the *only* thing standing between host and container was the mount namespace.
Share the directory and cross-boundary mutual exclusion is restored — note it worked despite the
container running as **root (uid 0)** and the host process as **uid 1000**, because the backing file is
mode `0666`.

The same reasoning applies to any mount-namespace boundary: two containers on the same host, a container
vs. the host, a `docker run` with `--tmpfs /tmp`, and Kubernetes pods (each pod's containers share a
namespace but pods do not). Different `/tmp` ⇒ different mutex, silently.

### 6. Permissions and cross-user behaviour

The PAL deliberately creates these world-accessible so that processes of *any* user can share them:

```cpp
const mode_t SharedMemoryHelpers::PermissionsMask_AllUsers_ReadWrite = ...;      // 0666
const mode_t SharedMemoryHelpers::PermissionsMask_AllUsers_ReadWriteExecute = ...; // 0777
...
mkdir(path, PermissionsMask_AllUsers_ReadWriteExecute);
fileDescriptor = Open(errors, path, openFlags, PermissionsMask_AllUsers_ReadWrite);
```

Source: <https://github.com/dotnet/runtime/blob/release/9.0/src/coreclr/pal/src/sharedmemory/sharedmemory.cpp>
(matches the observed `drwxrwxrwx` / `-rw-rw-rw-` in §3)

Microsoft Learn is explicit about the security consequence:

> **Caution:** By default, a named mutex is not restricted to the user that created it. Other users may
> be able to open and use the mutex, including interfering with the mutex by entering the mutex and not
> exiting it. **On Unix-like operating systems, the file system is used in the implementation of named
> mutexes, and other users may be able to interfere with named mutexes in more significant ways.** On
> Windows, to restrict access to specific users, you can use a constructor overload or `MutexAcl` and
> pass in a `MutexSecurity` when creating the named mutex. **On Unix-like operating systems, currently
> there is no way to restrict access to a named mutex.** Avoid using named mutexes without access
> restrictions on systems that might have untrusted users running code.

Source: <https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutex>

That "no way to restrict access" statement is a `net-9.0`-era truth that **.NET 10 partially fixes**:
`NamedWaitHandleOptions` with `CurrentUserOnly` / `CurrentSessionOnly` was added in .NET 10 (the API page
carries only `net-10.0` and `net-11.0` monikers), via
<https://github.com/dotnet/runtime/pull/112213>. Source:
<https://learn.microsoft.com/en-us/dotnet/api/system.threading.namedwaithandleoptions>

Note also that `/tmp` is normally sticky (`drwxrwxrwt`), which limits *deletion* by other users, but the
mutex files themselves are `0666` and therefore writable by anyone on the box.

Interesting related detail — the PAL relaxes its permission check on `/tmp` itself specifically because
of containers:

```cpp
// For system directories (such as TEMP_DIRECTORY_PATH), require sufficient permissions only for the
// current user. For instance, "docker run --mount ..." to mount /tmp to some directory on the host mounts
// the destination directory with the same permissions as the source directory, which may not include some
// permissions for other users. In the docker container, other user permissions are typically not relevant...
```

### 7. Name rules and limits on Unix

From `SharedMemoryId::SharedMemoryId(LPCSTR name)`:

- `Global\` prefix ⇒ global scope; `Local\` prefix (or **no** prefix) ⇒ session scope.
- Empty name after stripping the prefix ⇒ `SharedMemoryError::NameEmpty`.
- Name longer than `SHARED_MEMORY_MAX_FILE_NAME_CHAR_COUNT` ⇒ `NameTooLong`. That constant is
  `(_MAX_FNAME - 1)` and `#define _MAX_FNAME 256`, so **the practical limit is 255 characters** (excluding
  the prefix).
- Any `\` or `/` **inside** the name ⇒ `NameInvalid`. This matches the docs: *"The backslash (\\) is a
  reserved character in a mutex name."*
- Names map straight onto filenames, so they are **case-sensitive on Linux** (and typically
  case-insensitive on a default macOS filesystem) — a divergence from Windows, where kernel object names
  are case-insensitive by default.

Sources: <https://github.com/dotnet/runtime/blob/release/9.0/src/coreclr/pal/src/sharedmemory/sharedmemory.cpp>,
<https://github.com/dotnet/runtime/blob/release/9.0/src/coreclr/pal/inc/pal.h>,
<https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutex>

### 8. ⚠ The default scope on Unix is the POSIX session, not the machine

`AppendSessionDirectoryName` uses `GetCurrentSessionId()`:

```cpp
if (IsSessionScope())
    return path.Append(SHARED_MEMORY_SESSION_DIRECTORY_NAME_PREFIX)
        && SharedMemoryHelpers::AppendUInt32String(path, GetCurrentSessionId());
else
    return path.Append(SHARED_MEMORY_GLOBAL_DIRECTORY_NAME);
```

and on Unix that value is set once at PAL init from `getsid(2)`:

```cpp
gSID = getsid(gPID);
```

Sources: <https://github.com/dotnet/runtime/blob/release/9.0/src/coreclr/pal/src/sharedmemory/sharedmemory.cpp>,
<https://github.com/dotnet/runtime/blob/release/9.0/src/coreclr/pal/src/init/pal.cpp>

The docs say the same thing in one short sentence that is very easy to skim past:

> When the `Local` namespace is specified, which is also the default when no namespace is specified, the
> synchronization object can be shared with processes in the same session. On Windows, a session is a
> login session, and services typically run in a different non-interactive session. **On Unix-like
> operating systems, each shell has its own session.**

Source: <https://learn.microsoft.com/en-us/dotnet/api/system.threading.namedwaithandleoptions>

**Verified.** Same host, same user, same unprefixed name, two processes in *different* POSIX sessions
(via `setsid`):

```
proc A (own session): pid=3548392 SessionScopeDemo  ACQUIRED
proc B (own session): pid=3548645 SessionScopeDemo  ACQUIRED     ← both got it
```

Control, same POSIX session:

```
proc A: pid=3549780 SessionScopeDemo2  ACQUIRED
proc B: pid=3549827 SessionScopeDemo2  BLOCKED-timeout           ← correct exclusion
```

Consequence: `new Mutex(false, "MyApp")` as a "only one copy of this app per machine" guard **does not
work on Linux** across systemd units, cron jobs, SSH sessions, or a daemon vs. an interactive shell.
You need the `Global\` prefix (or, on .NET 10+, `NamedWaitHandleOptions { CurrentSessionOnly = false }`).

### 9. Windows: `Global\` vs `Local\`

> On a server that is running Terminal Services, a named system mutex can have two levels of visibility.
> If its name begins with the prefix `Global\`, the mutex is visible in all terminal server sessions. If
> its name begins with the prefix `Local\`, the mutex is visible only in the terminal server session where
> it was created. In that case, a separate mutex with the same name can exist in each of the other terminal
> server sessions on the server. **If you do not specify a prefix when you create a named mutex, it takes
> the prefix `Local\`.** Within a terminal server session, two mutexes whose names differ only by their
> prefixes are separate mutexes, and both are visible to all processes in the terminal server session.
> That is, the prefix names `Global\` and `Local\` describe the scope of the mutex name relative to
> terminal server sessions, not relative to processes.

Source: <https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutex>

Practical Windows implications the docs imply but don't spell out: a Windows **service** runs in session 0
while an interactive user runs in session 1+, so unprefixed names do not bridge them — structurally the
same trap as §8, for a different reason. Creating a `Global\` object on Windows also generally requires
the `SeCreateGlobalPrivilege`. Windows object names are also limited to `MAX_PATH` (260) characters.
See also <https://learn.microsoft.com/en-us/windows/win32/sync/object-names>.

### 10. `AbandonedMutexException` — and the case where it silently doesn't fire

**Documented behaviour.** `Mutex` remarks:

> If a thread terminates while owning a mutex, the mutex is said to be abandoned. The state of the mutex
> is set to signaled, and the next waiting thread gets ownership. Beginning in version 2.0 of the .NET
> Framework, an `AbandonedMutexException` is thrown in the next thread that acquires the abandoned mutex.

> **Caution:** An abandoned mutex often indicates a serious error in the code. When a thread exits without
> releasing the mutex, the data structures protected by the mutex might not be in a consistent state. The
> next thread to request ownership of the mutex can handle this exception and proceed, if the integrity of
> the data structures can be verified. In the case of a system-wide mutex, an abandoned mutex might indicate
> that an application has been terminated abruptly.

`AbandonedMutexException` remarks:

> When a thread abandons a mutex, the exception is thrown in the next thread that acquires the mutex. The
> thread might acquire the mutex because it was already waiting on the mutex or because it enters the mutex
> at a later time. [...] **The wait still succeeds** — "Whether or not the exception was thrown, the current
> thread owns the mutex, and must release it."

Note the properties: `MutexIndex` and `Mutex` are populated for `WaitAny`; for `WaitAll`, `MutexIndex` is
always `-1` and `Mutex` is always `null`.

Sources: <https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutex>,
<https://learn.microsoft.com/en-us/dotnet/api/system.threading.abandonedmutexexception>

**Is it the same on Unix? Mostly yes — with an important hole.**

Thread-exit abandonment is implemented explicitly (the owning thread keeps a linked list of held named
mutexes and abandons them on exit). Process-crash abandonment relies on pthread robustness (`EOWNERDEAD`
→ `pthread_mutex_consistent`) or, on the `flock` path, on the kernel releasing the file lock plus an
owner-info check in shared memory.

**But** the shared-memory file's *lifetime* is also managed by file locks, and the design comment says:

> Upon creation, if the shared memory file already exists, an exclusive file lock is attempted on it, to
> see if the file data is valid. **If no other processes have the mutex open, the file is reinitialized.**

So if the crashing process was the **only** process with the mutex open, the next process to come along
gets an exclusive lock, decides the state is stale, reinitialises the file — and the abandonment signal
is destroyed before anyone can observe it.

**Verified, and reproduced.** Process A acquires `AbandonDemo` and is `SIGKILL`ed.

*Without* any other process keeping the mutex open (2/2 runs):

```
--- control run 1: NO keep-open process ---   ACQUIRED     ← no AbandonedMutexException
--- control run 2: NO keep-open process ---   ACQUIRED     ← no AbandonedMutexException
```

*With* a third process C holding the mutex open (but never acquiring it) across the whole test:

```
[wait] pid=3555611 name=AbandonDemo3
ABANDONED -> AbandonedMutexException: The wait completed due to an abandoned mutex.
```

Same code, same machine, same kill. The only difference was whether the backing file stayed referenced.

This matters directly for the classic use case: "detect that the previous instance of my app crashed by
catching `AbandonedMutexException` on the single-instance mutex." On Linux, in the single-instance case
specifically — where by definition there *is* no other holder — **you will not get the exception.**

---

## Sources

| Source | What it is |
|---|---|
| <https://github.com/dotnet/runtime/blob/release/9.0/src/coreclr/pal/src/include/pal/mutex.hpp> | **Source code** — the named-mutex design comment; `NAMED_MUTEX_USE_PTHREAD_MUTEX` selection. The single best primary source for this question. |
| <https://github.com/dotnet/runtime/blob/release/9.0/src/coreclr/pal/src/synchobj/mutex.cpp> | **Source code** — `pthread_mutexattr_setpshared`/`setrobust`/`settype`, `EOWNERDEAD` → `pthread_mutex_consistent`, abandon handling, `CreateOrOpen`. |
| <https://github.com/dotnet/runtime/blob/release/9.0/src/coreclr/pal/src/sharedmemory/sharedmemory.cpp> | **Source code** — `Global\`/`Local\` parsing, name validation, `mmap`/`ftruncate`/`flock`, `0666`/`0777` permission masks, the docker `--mount` comment. |
| <https://github.com/dotnet/runtime/blob/release/9.0/src/coreclr/pal/src/include/pal/sharedmemory.h> | **Source code** — `.dotnet/shm`, `.dotnet/lockfiles`, `global`, `session` path constants; `SHARED_MEMORY_MAX_FILE_NAME_CHAR_COUNT`. |
| <https://github.com/dotnet/runtime/blob/release/9.0/src/coreclr/pal/src/include/pal/palinternal.h> | **Source code** — `#define TEMP_DIRECTORY_PATH "/tmp/"` (Android: `/data/local/tmp/`). |
| <https://github.com/dotnet/runtime/blob/release/9.0/src/coreclr/pal/src/init/pal.cpp> | **Source code** — `gSharedFilesPath` fallback to `TEMP_DIRECTORY_PATH`; `gSID = getsid(gPID)`. |
| <https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Threading/NamedMutex.Unix.cs> | **Source code** — the .NET 11 managed rewrite of the same design (see Unverified). |
| <https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/IO/SharedMemoryManager.Unix.cs> | **Source code** — managed counterpart of the PAL shared-memory manager. |
| <https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutex> | **Official doc** — `Global\`/`Local\` and terminal-server sessions; the Unix filesystem/cross-user Caution; reserved backslash; abandoned-mutex behaviour. |
| <https://learn.microsoft.com/en-us/dotnet/api/system.threading.abandonedmutexexception> | **Official doc** — when it's thrown, what it implies about protected state, `MutexIndex`/`Mutex` semantics. |
| <https://learn.microsoft.com/en-us/dotnet/api/system.threading.namedwaithandleoptions> | **Official doc** — .NET 10+ `CurrentUserOnly`/`CurrentSessionOnly`; *"On Unix-like operating systems, each shell has its own session."* |
| <https://github.com/dotnet/runtime/pull/112213> | **Implementation PR** — adds `CurrentUserOnly`/`CurrentSessionOnly` for named `Mutex`/`Semaphore`/`EventWaitHandle`. |
| <https://github.com/dotnet/runtime/issues/65929> | **Issue** — "Review workaround for Named mutex: Use flock instead of pthread process-shared mutex". Background on the platform exclusions. |
| <https://github.com/dotnet/runtime/issues/10519> | **Issue** — the FreeBSD process-shared robust mutex limitation cited in the source comment. |
| <https://learn.microsoft.com/en-us/windows/win32/sync/object-names> | **Official doc** — Windows kernel object namespaces (`Global\`, `Local\`, session isolation). |

---

## Talk-ready points

- "On Linux there is no such thing as a named mutex in the kernel — .NET builds one. `new Mutex(false, "X")`
  creates a **file**: `/tmp/.dotnet/shm/session<sid>/X`, `mmap`ped `MAP_SHARED`, with a `pthread`
  process-shared **robust recursive** mutex living inside it. On macOS and FreeBSD, where robust pthread
  mutexes aren't usable, it degrades to `flock` on a second empty file under `/tmp/.dotnet/lockfiles/` —
  and because `flock` has no timed wait, timed waits there **poll** on a 100 ms loop."

- "So why doesn't your container contend with the host? Not magic, not namespaces-for-mutexes — the mutex
  *is* the file, and a container has its own mount namespace, so it has its own `/tmp`, so it's a different
  file. I ran it: host and container both asked for `Global\DockerDemo`, **both got it**. Then I re-ran with
  `-v /tmp:/tmp` and the container blocked correctly. The mount namespace was the only thing between them."

- "Here's the one that will bite someone in this room. On Unix the **default scope is the POSIX session**,
  from `getsid(2)` — the docs put it in eight words: *'On Unix-like operating systems, each shell has its
  own session.'* I verified it: two processes, same user, same host, same name, different `setsid` sessions
  — **both acquired**. Same test in one session, second one blocked. So `new Mutex(false, "MyApp")` as a
  single-instance guard silently does nothing across systemd units, cron, or two SSH logins. You need
  `Global\`."

- "These files are created **world read-write** — mode `0666`, in directories at `0777` — deliberately, so
  any user can share them. Microsoft says the quiet part in a Caution box: *'On Unix-like operating systems
  ... other users may be able to interfere with named mutexes in more significant ways'* and *'currently
  there is no way to restrict access to a named mutex.'* Any user on that box can grab your mutex and never
  let go. .NET 10 finally added `NamedWaitHandleOptions.CurrentUserOnly` to fix it."

- "`AbandonedMutexException` means: *the previous owner died holding this, your wait succeeded anyway, and
  the state it was protecting may be garbage.* You own the mutex whether or not the exception was thrown, so
  you still have to release it. But on Linux there's a hole — the backing file's lifetime is refcounted by
  file locks, and if the crashed process was the **only** one with it open, the next process reinitialises
  the file and the abandonment is erased. I reproduced both sides: with a third process keeping it open I
  got the exception every time; with nobody else holding it, I got a clean `ACQUIRED`, twice. Which means
  the single-instance-crash-detection pattern — the exact case where there is no other holder — is the case
  where it doesn't work."

- "Limits worth knowing: name is capped at **255 characters** (it's a filename), backslash and forward slash
  are illegal inside it, and names are **case-sensitive on Linux** because they're filenames — Windows kernel
  object names are not. Also: if a process is killed hard, the design comment admits the leftover file may
  just stay in `/tmp`. The author wrote, verbatim, *'I haven't found anything that can be done about that.'*"

---

## Unverified / open

- **.NET 11 has rewritten this in managed code and I did not test it.** On `dotnet/runtime` `main` the PAL
  C++ implementation has been replaced by
  `src/libraries/System.Private.CoreLib/src/System/Threading/NamedMutex.Unix.cs` and
  `src/libraries/System.Private.CoreLib/src/System/IO/SharedMemoryManager.Unix.cs`. I read enough of the
  managed port to confirm it preserves the same design (pthread-vs-fallback selection, shared-memory
  process data, ownership chain for abandon detection), but I did **not** verify that the file paths,
  permissions, session scoping, or the lost-abandonment behaviour are byte-for-byte identical in .NET 11.
  Everything empirical here was run on .NET 10 (runtime 10.0.10), which still uses the PAL.
- **The `flock` fallback path was not exercised.** My box took the pthread path (no `lockfiles` directory
  ever appeared). The macOS/FreeBSD behaviour — including whether the lost-abandonment hole in §10 behaves
  the same there — is from source reading only.
- **Whether the lost-abandonment behaviour is considered a bug or by design.** I did not find a tracking
  issue for it. It follows directly from the documented lifetime scheme, but I am inferring the intent.
- **Windows-side claims in §9 beyond the quoted docs** — specifically that `SeCreateGlobalPrivilege` is
  required for `Global\` and the `MAX_PATH` name limit — come from the Win32 object-names documentation and
  were **not** tested; no Windows machine was available.
- **`GetCurrentSessionId` on macOS.** I confirmed `gSID = getsid(gPID)` in `pal.cpp` for the Unix build but
  did not check for Apple-specific overrides, nor how the app-container path interacts with session scoping.
- **Cross-user contention was only tested root-in-container vs. uid 1000 on the host**, which succeeded via
  the shared `/tmp` mount. I did not test two genuinely different non-root users on the same host.
