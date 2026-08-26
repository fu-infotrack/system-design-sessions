# Q6 — `SemaphoreSlim` vs `Monitor` fairness

## Summary

Neither is documented as fair, and neither is fair in the source. Microsoft Learn states outright that
for `SemaphoreSlim` "there is no guaranteed order, such as FIFO or LIFO, that controls when threads
enter the semaphore", and the `Monitor` docs never promise ordering for `Enter` at all — the closest
official statement is the `Monitor.Pulse` remark that the next thread to acquire is "not necessarily
the thread that was pulsed". Internally `SemaphoreSlim` maintains **two** waiter populations — an
intrusive FIFO linked list for `WaitAsync` callers and a `Monitor.Wait`/`Pulse` queue for `Wait`
callers — with a partial fairness bridge between them, plus a spin-before-lock fast path that lets a
brand-new arrival barge past everyone. And the over-release exception is **`SemaphoreFullException`**;
`SemaphoreMaxCountExceededException` does not exist in .NET.

> Verification environment: .NET SDK 10.0.302 / runtime 10.0.10 on Linux (WSL2). Source references are
> `dotnet/runtime` `main` unless noted; I spot-checked that the relevant `SemaphoreSlim` logic is
> unchanged from the .NET 9 shape.

---

## Findings

### 1. Is `SemaphoreSlim` documented as FIFO / fair? — No, explicitly not

Microsoft Learn, `SemaphoreSlim` Remarks:

> When the count reaches zero, subsequent calls to one of the `Wait` methods block until other threads
> release the semaphore. **If multiple threads are blocked, there is no guaranteed order, such as FIFO
> or LIFO, that controls when threads enter the semaphore.**

Source: <https://learn.microsoft.com/en-us/dotnet/api/system.threading.semaphoreslim>

### 2. What the `SemaphoreSlim` source actually does

Source for everything in this section:
<https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Threading/SemaphoreSlim.cs>

**There are two distinct waiter mechanisms, not one queue.**

*Async waiters* live in an intrusive doubly-linked list — strictly FIFO by construction:

```csharp
private TaskNode? m_asyncHead;
private TaskNode? m_asyncTail;

private sealed class TaskNode : Task<bool>
{
    internal TaskNode? Prev, Next;
    ...
}
```

`CreateAndAddAsyncWaiter()` appends at `m_asyncTail`; `Release` dequeues from `m_asyncHead`. So among
async waiters, release order is FIFO.

*Sync waiters* do not use that list at all. They block on `Monitor.Wait(m_lockObjAndDisposed, ...)` and
are woken by `Monitor.Pulse` — inheriting whatever ordering the monitor gives, which is not specified
(see §4):

```csharp
bool waitSuccessful = Monitor.Wait(m_lockObjAndDisposed, monitorWaitMilliseconds);
```

```csharp
m_countOfWaitersPulsedToWake += waitersToNotify;
for (int i = 0; i < waitersToNotify; i++)
{
    Monitor.Pulse(m_lockObjAndDisposed);
}
```

**There *is* a deliberate fairness bridge between the two.** When a *synchronous* `Wait` arrives and
async waiters are already queued, it converts itself into an async wait and blocks on that task, so it
queues behind them rather than jumping in front:

```csharp
Monitor.Enter(m_lockObjAndDisposed, ref lockTaken);
m_waitCount++;

// If there are any async waiters, for fairness we'll get in line behind
// then by translating our synchronous wait into an asynchronous one that we
// then block on (once we've released the lock).
if (m_asyncHead is not null)
{
    asyncWaitTask = WaitAsyncCore(millisecondsTimeout, cancellationToken);
}
```

That comment — *"for fairness we'll get in line behind"* — is the only place in the type that claims
fairness, and it only covers this one direction (sync arriving while async waiters exist).

**But there are at least two documented-in-code barging paths:**

(a) A newly arriving *sync* waiter spins for a positive count **before** it takes the internal lock, so
it can consume a count that `Release` just made available for an already-queued waiter:

```csharp
// Perf: first spin wait for the count to be positive.
// This additional amount of spinwaiting in addition
// to Monitor.Enter()'s spinwaiting has shown measurable perf gains in test scenarios.
if (m_currentCount == 0)
{
    int spinCount = SpinWait.SpinCountForSpinBeforeWait * 4;
    SpinWait spinner = default;
    while (spinner.Count < spinCount) { spinner.SpinOnce(sleep1Threshold: -1); if (m_currentCount != 0) break; }
}
```

(b) A newly arriving *async* waiter takes an available count immediately without consulting the async
queue at all:

```csharp
lock (m_lockObjAndDisposed)
{
    // If there are counts available, allow this waiter to succeed.
    if (m_currentCount > 0)
    {
        --m_currentCount;
        ...
        return Task.FromResult(true);
    }
    ...
}
```

**`Release` prioritises sync waiters over async waiters.** It pulses sync waiters first, then releases
async waiters only up to `currentCount - waitCount` — i.e. it reserves one count for every outstanding
*synchronous* waiter before any async waiter is completed:

```csharp
if (m_asyncHead is not null)
{
    int maxAsyncToRelease = currentCount - waitCount;
    while (maxAsyncToRelease > 0 && m_asyncHead is not null) { ... waiterTask.TrySetResult(result: true); }
}
```

with the accompanying comment:

```
// Now signal to any asynchronous waiters, if there are any.  While we've already
// signaled the synchronous waiters, we still hold the lock, and thus
// they won't have had an opportunity to acquire this yet.  So, when releasing
// asynchronous waiters, we assume that all synchronous waiters will eventually
// acquire the semaphore.  That could be a faulty assumption if those synchronous
// waits are canceled, but the wait code path will handle that.
```

**Net answer to "do async and sync waiters share one queue?":** No. They are two mechanisms with a
one-directional courtesy rule (a sync arrival defers to existing async waiters) and an opposite priority
rule inside `Release` (queued sync waiters are counted before async waiters are completed). It is not a
single unified FIFO queue.

**Empirical check.** With 20 sequential `WaitAsync()` calls followed by 20 sequential `Release()` calls,
completion order was strictly FIFO (`0,1,2,...,19`) — consistent with the linked list. That is a
best-case, contention-free arrangement and is **not** evidence of a guarantee.

### 3. `SemaphoreSlim` is non-reentrant, and `Release` may be called by any thread

Microsoft Learn, `SemaphoreSlim` Remarks (Important block):

> The `SemaphoreSlim` class **doesn't enforce thread or task identity** on calls to the `Wait`,
> `WaitAsync`, and `Release` methods. In addition, if the `SemaphoreSlim(Int32)` constructor is used to
> instantiate the `SemaphoreSlim` object, the `CurrentCount` property can increase beyond the value set
> by the constructor. It is the programmer's responsibility to ensure that calls to `Wait` or `WaitAsync`
> methods are appropriately paired with calls to `Release` methods.

Source: <https://learn.microsoft.com/en-us/dotnet/api/system.threading.semaphoreslim>

For contrast, `Mutex` is documented the opposite way:

> The `Mutex` class **enforces thread identity**, so a mutex can be released only by the thread that
> acquired it. By contrast, the `Semaphore` class does not enforce thread identity.

Source: <https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutex>

"Doesn't enforce thread identity" is exactly what makes it (a) usable as an async mutex across `await`
boundaries and thread hops, and (b) **non-reentrant** — a second `Wait()` on the same thread just
consumes another count, and with `SemaphoreSlim(1, 1)` that means self-deadlock.

**Confirmed empirically:**

```
[reentrant?] second Wait(200ms) returned False   // same thread, SemaphoreSlim(1,1) — no recursion
[release from foreign thread] OK, CurrentCount = 1   // Wait() on main thread, Release() on another thread
```

### 4. Is `Monitor` documented as fair? — No

There is **no statement anywhere in the current `Monitor` docs that `Monitor.Enter` is FIFO.** The docs
describe the data structures but never promise an ordering for lock acquisition:

> The following information is maintained for each synchronized object:
> - A reference to the thread that currently holds the lock.
> - A reference to a ready queue, which contains the threads that are ready to obtain the lock.
> - A reference to a waiting queue, which contains the threads that are waiting for notification of a
>   change in the state of the locked object.

Source: <https://learn.microsoft.com/en-us/dotnet/api/system.threading.monitor>

The authoritative "it is not guaranteed" statement lives on `Monitor.Pulse`:

> The thread that currently owns the lock on the specified object invokes this method to signal the next
> thread in line for the lock. Upon receiving the pulse, the waiting thread is moved to the ready queue.
> When the thread that invoked `Pulse` releases the lock, **the next thread in the ready queue (which is
> not necessarily the thread that was pulsed) acquires the lock.**

Source: <https://learn.microsoft.com/en-us/dotnet/api/system.threading.monitor.pulse>

The `Wait`/`Pulse` *queue* is described as ordered ("to be moved, the thread must be at the head of the
waiting queue"), but that is the condition-variable queue, not the lock-acquisition queue.

Corroborating evidence from the runtime source: `System.Threading.Lock` — the modern managed lock,
which is the direct sibling of the monitor implementation and is what backs NativeAOT's monitors —
documents barging in a comment:

```
// Spinning helps to reduce waiter starvation. Since other non-waiter threads can take the lock while
// there are waiters (see State.TryLock()), once a waiter wakes it will be able to better compete with
// other spinners for the lock.
```

Source: <https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Threading/Lock.cs>

A thread that never entered the wait queue can acquire the lock while waiters are queued. That is the
textbook definition of an unfair (barging) lock — and it is deliberate, because barging is what gives
you throughput on modern hardware.

**Empirical note (weak evidence, reported honestly):** a 6-thread, 1.5 s tight-loop acquire/release
microbenchmark (single run, Release, WSL2 — *not* BenchmarkDotNet) showed no gross starvation, but the
per-thread spread for `Monitor` was the widest of the three:

```
Monitor/lock     total=  370,736  min= 48,221  max= 88,323  max/min=1.8x
Threading.Lock   total=  378,300  min= 59,526  max= 65,380  max/min=1.1x
SemaphoreSlim    total=  273,879  min= 40,604  max= 50,860  max/min=1.3x
```

Do not present this as a fairness measurement. It shows "nobody starved outright in this run", nothing
more.

### 5. Over-release: `SemaphoreFullException`, not `SemaphoreMaxCountExceededException`

**`SemaphoreMaxCountExceededException` does not exist in .NET.** The type thrown is
`System.Threading.SemaphoreFullException`, and it is thrown from `Release(int)` when the release would
push the count above `maxCount`:

```csharp
// If the release count would result exceeding the maximum count, throw SemaphoreFullException.
if (m_maxCount - currentCount < releaseCount)
{
    throw new SemaphoreFullException();
}
```

with the XML doc on `Release(int)`:

```csharp
/// <exception cref="SemaphoreFullException">The <see cref="SemaphoreSlim"/> has
/// already reached its maximum size.</exception>
```

Source: <https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Threading/SemaphoreSlim.cs>

**The critical caveat:** the check is against `m_maxCount`, which is only meaningful if you used the
**two-argument constructor**. `new SemaphoreSlim(1)` sets `maxCount` to `int.MaxValue`, so over-release
throws nothing at all and silently corrupts your concurrency limit.

**Confirmed empirically:**

```
[over-release, maxCount set]  System.Threading.SemaphoreFullException
[over-release, no maxCount]   no throw; CurrentCount = 3
```

That second line is the bug: `new SemaphoreSlim(1)` used as a mutex, `Release()` called twice by
mistake in a `finally`, and now two threads can be in your critical section forever, with no exception
and no log line.

`Release()`/`Release(int)` return the **previous** count, and the full exception list is
`ArgumentOutOfRangeException` (releaseCount < 1), `SemaphoreFullException`, and `ObjectDisposedException`.

### 6. `WaitAsync` — practical notes

- `WaitAsync` overloads mirror `Wait` (`CancellationToken`, `int`, `TimeSpan` combinations).
  Source: <https://learn.microsoft.com/en-us/dotnet/api/system.threading.semaphoreslim>
- The waiter `TaskNode` is created with `TaskCreationOptions.RunContinuationsAsynchronously`, so
  completing a waiter inside `Release` does not run the continuation inline under the semaphore's
  internal lock.
- `Dispose` does **not** fault outstanding waiters; the docs are explicit that `Dispose()` "must be used
  only when all other operations on the `SemaphoreSlim` have completed".
- `SemaphoreSlim` is the *only* one of these primitives that can be held across an `await`, precisely
  because it does not track thread identity — which is the same property that makes it non-reentrant and
  makes a mismatched `Release` undetectable.

---

## Sources

| Source | What it is |
|---|---|
| <https://learn.microsoft.com/en-us/dotnet/api/system.threading.semaphoreslim> | **Official doc** — "no guaranteed order, such as FIFO or LIFO"; "doesn't enforce thread or task identity"; `CurrentCount` can exceed the ctor value. |
| <https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Threading/SemaphoreSlim.cs> | **Source code** — async FIFO linked list, `Monitor.Wait`/`Pulse` sync path, the "for fairness we'll get in line behind" bridge, the spin-before-lock barging path, `SemaphoreFullException`. |
| <https://learn.microsoft.com/en-us/dotnet/api/system.threading.monitor> | **Official doc** — ready queue / waiting queue description; no FIFO promise for `Enter`. |
| <https://learn.microsoft.com/en-us/dotnet/api/system.threading.monitor.pulse> | **Official doc** — "the next thread in the ready queue (which is not necessarily the thread that was pulsed) acquires the lock". The authoritative non-fairness statement. |
| <https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Threading/Lock.cs> | **Source code** — explicit barging comment: non-waiter threads can take the lock while there are waiters. |
| <https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutex> | **Official doc** — used for contrast: `Mutex` *does* enforce thread identity. |
| <https://learn.microsoft.com/en-us/dotnet/api/system.threading.semaphoreslim.release> | **Official doc** — `Release` overloads and exception list. |

---

## Talk-ready points

- "Neither of these is fair, and both are honest about it if you read closely. The `SemaphoreSlim` docs
  say it in one sentence: *'If multiple threads are blocked, there is no guaranteed order, such as FIFO
  or LIFO, that controls when threads enter the semaphore.'* For `Monitor`, the docs never promise
  ordering for `Enter` at all — the closest they get is on `Monitor.Pulse`: *'the next thread in the
  ready queue, which is not necessarily the thread that was pulsed, acquires the lock.'*"

- "These locks *barge* on purpose. The comment in the runtime's `Lock` implementation says it outright:
  non-waiter threads can take the lock while there are waiters. A thread that just walked up and spun for
  a microsecond can beat a thread that's been queued for a millisecond. That's not a bug — barging is
  what buys you throughput, because handing the lock to a sleeping thread costs you a context switch."

- "`SemaphoreSlim` doesn't have one queue, it has two. `WaitAsync` callers go into an intrusive FIFO
  linked list. `Wait` callers block on `Monitor.Wait` and get pulsed. There's exactly one fairness bridge
  — the source comment literally reads *'If there are any async waiters, for fairness we'll get in line
  behind them'* — and it only runs in one direction. Meanwhile `Release` reserves a count for every
  synchronous waiter *before* it completes any async waiter. So if you mix `Wait` and `WaitAsync` on the
  same semaphore, your async callers are the ones who lose."

- "If you over-release, you get **`SemaphoreFullException`** — there is no such type as
  `SemaphoreMaxCountExceededException`. But here's the part that actually bites: that check is against
  `maxCount`, and `new SemaphoreSlim(1)` sets `maxCount` to `int.MaxValue`. I ran it: with
  `new SemaphoreSlim(1, 1)` you get the exception; with `new SemaphoreSlim(1)` a double-release throws
  nothing and `CurrentCount` quietly climbs to 3. Your mutex is now a semaphore of three and nothing
  told you. **Always pass both arguments.**"

- "`SemaphoreSlim` does not enforce thread identity — the docs call that out in an Important box. That
  single property is why you can hold it across an `await`, why any thread can `Release()` one you never
  `Wait()`ed on, and why it is **not reentrant**. I verified all three. A second `Wait()` on the same
  thread against `SemaphoreSlim(1,1)` just blocks. It's not a lock that knows who you are; it's a counter."

- "So the honest summary for choosing: `Monitor` is thread-affine, reentrant, sync-only, unfair.
  `SemaphoreSlim` is thread-agnostic, non-reentrant, async-capable, unfair. If your design needs
  *guaranteed* FIFO ordering, neither of these gives it to you and you need to build the queue yourself."

---

## Unverified / open

- **Whether CoreCLR's `Monitor.Enter` shares an implementation with the managed `System.Threading.Lock`
  in .NET 9/10.** I used the `Lock.cs` barging comment as corroboration for monitor-style barging; that
  is accurate for NativeAOT (where `Lock` backs monitors) but I did not confirm the CoreCLR wiring. The
  `Monitor.Pulse` doc quote stands on its own regardless.
- **Reachability of the `WaitAsyncCore` barging path.** The code unconditionally takes an available count
  before consulting `m_asyncHead`. Whether `m_currentCount > 0` can actually coexist with a non-empty
  async queue depends on `Release`'s `maxAsyncToRelease = currentCount - waitCount` accounting; I read the
  code but did not construct a test that demonstrably starves a queued async waiter.
- **Historical Microsoft docs claiming the monitor queue is "roughly FIFO but not guaranteed".** I could
  not locate such wording in any *current* Microsoft Learn page. If it existed it was on older MSDN and I
  did not find an archived copy. Don't attribute that phrasing to Microsoft on stage — quote the
  `Monitor.Pulse` sentence instead, which I did verify.
- **The microbenchmark numbers** in §4 are a single un-warmed run on WSL2 without BenchmarkDotNet. Treat
  as anecdote, not measurement.
- Results were produced on .NET 10 (runtime 10.0.10), not .NET 9. `SemaphoreSlim`'s structure is
  long-standing but I did not diff .NET 9 vs .NET 10 line by line.
