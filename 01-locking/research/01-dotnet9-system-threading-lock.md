# Q1 — .NET 9 `System.Threading.Lock`

## Summary

When the compiler statically knows the expression is of type `System.Threading.Lock`, `lock (x)` is
lowered to `using (x.EnterScope()) { ... }` — a `try`/`finally` around a `Lock.Scope` `ref struct`
whose `Dispose()` exits the lock. If the same instance is reached through a variable typed `object`
(or a generic `T`), the compiler emits the *old* `Monitor.Enter`/`Monitor.Exit` lowering instead, and
the two mechanisms **do not exclude each other** — two threads can be inside the "same" lock at once.
Roslyn warns about this with **CS9216** (a compiler warning, not a separate analyzer package), and the
feature requires **C# 13 + .NET 9**. The lock is reentrant, you cannot `await` inside it, and — worth
knowing before you repeat it on stage — there is **no official Microsoft benchmark** behind the
widely-quoted "~25% faster" figure.

> Verification environment for the empirical results below: .NET SDK 10.0.302 / runtime 10.0.10 on
> Linux (WSL2, kernel 6.18), `LangVersion` pinned to 13.0. The feature and diagnostics are identical
> in .NET 9; where a result is .NET-10-specific it is called out.

---

## Findings

### 1. The lowering: `lock (x)` where `x` is `System.Threading.Lock`

The csharplang speclet amends §13.13 of the C# standard and states the transformation as *precisely
equivalent*:

> A `lock` statement of the form `lock (x) { ... }`
> 1. **where `x` is an expression of type `System.Threading.Lock`, is precisely equivalent to:**
>    ```cs
>    using (x.EnterScope())
>    {
>        ...
>    }
>    ```

Source: <https://github.com/dotnet/csharplang/blob/main/proposals/csharp-13.0/lock-object.md>

The Microsoft Learn `lock` statement reference says the same thing and spells out the `ref struct`:

> When the compiler knows that `x` is of the type `System.Threading.Lock`, it's precisely equivalent to:
> `using (x.EnterScope()) { ... }`
> The object returned by `Lock.EnterScope()` is a `ref struct` that includes a `Dispose()` method. The
> generated `using` statement ensures the scope is released even if an exception is thrown within the
> body of the `lock` statement.

Source: <https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/lock>

**Confirmed.** So the emitted shape is `Lock.Scope s = x.EnterScope(); try { ... } finally { s.Dispose(); }`.
Because `Scope` is a struct, there is no null check in the `finally` (unlike the `IDisposable` `using`
pattern). In the runtime source, `EnterScope` captures the current managed thread id up front so that
`Dispose` can exit without a second thread-static lookup:

```csharp
public Scope EnterScope() => new Scope(this, EnterAndGetCurrentThreadId());
```

Source: <https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Threading/Lock.cs>

### 2. The `object`-typed fallback: `Monitor.Enter`/`Monitor.Exit`

The speclet's clause 2 keeps the historical behaviour for any other reference type, and Microsoft Learn
gives the exact fallback lowering:

```csharp
object __lockObj = x;
bool __lockWasTaken = false;
try
{
    System.Threading.Monitor.Enter(__lockObj, ref __lockWasTaken);
    // Your code...
}
finally
{
    if (__lockWasTaken) System.Threading.Monitor.Exit(__lockObj);
}
```

Source: <https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/lock>

The `Lock` API docs are blunt about the consequence:

> When using the C# `lock` keyword or similar to enter and exit a lock, the type of the expression must
> be precisely `System.Threading.Lock`. If the type of the expression is anything else, such as `Object`
> or a generic type like `T`, a different implementation that is not interchangeable can be used instead
> (such as `Monitor`).

Source: <https://learn.microsoft.com/en-us/dotnet/api/system.threading.lock>

**Confirmed empirically.** Running a program that locks the *same* `Lock` instance through both a
`Lock`-typed and an `object`-typed variable:

```
[Lock-typed]   Monitor.IsEntered(l) = False
[Lock-typed]   l.IsHeldByCurrentThread = True
[object-typed] Monitor.IsEntered(o) = True
[object-typed] l.IsHeldByCurrentThread = False
[cross]        other thread l.TryEnter() while Monitor holds it = True
```

The last line is the money shot: one thread held the object *via `Monitor`*, and a second thread
successfully entered the *same instance* via `Lock.TryEnter()`. Mutual exclusion is silently gone.
These are two independent locks living on one object.

### 3. The diagnostic: CS9216 (compiler warning), and what CS9217 actually is

**CS9216** is the only `Lock`-specific diagnostic Roslyn emits. It is a plain compiler warning built
into Roslyn, not a separate analyzer NuGet package. Exact resource text:

```xml
<data name="WRN_ConvertingLock" xml:space="preserve">
  <value>A value of type 'System.Threading.Lock' converted to a different type will use likely unintended monitor-based locking in 'lock' statement.</value>
</data>
```

Sources:
- <https://github.com/dotnet/roslyn/blob/main/src/Compilers/CSharp/Portable/CSharpResources.resx> (`WRN_ConvertingLock`)
- <https://github.com/dotnet/roslyn/blob/main/src/Compilers/CSharp/Portable/Errors/ErrorCode.cs> (`WRN_ConvertingLock = 9216`)
- <https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-messages/lock-semantics>

The speclet explains the rule precisely: the warning is attached to the *implicit reference conversion*,
not to the `lock` statement, and it fires for `object`, `dynamic`, base classes and interfaces:

> A warning is reported when the *reference_type* is known to be `System.Threading.Lock`.
> [...] Note that this warning occurs even for equivalent explicit conversions.

with one documented carve-out:

> The compiler avoids reporting the warning in some cases when the instance cannot be locked after
> converting to `object`: when the conversion is implicit and part of an object equality operator
> invocation.

Escape hatches listed in the speclet: `#pragma warning disable`, using `Monitor` APIs directly, or
laundering through a generic such as `object AsObject<T>(T l) => (object)l;`.

Source: <https://github.com/dotnet/csharplang/blob/main/proposals/csharp-13.0/lock-object.md>

**Confirmed empirically**, including the "conversion site, not lock site" nuance — in my test the
warning landed on `object o = l;` *and* on passing `l` to `Monitor.IsEntered(object)`, but **not** on
the `lock (o)` statement itself:

```
Program.cs(8,82): warning CS9216: ...   // the argument to Monitor.IsEntered(object)
Program.cs(13,12): warning CS9216: ...  // object o = l;
Program.cs(30,13): warning CS9216: ...  // object o2 = l;
```

> ### ⚠ Correction-worthy: the Microsoft Learn page on CS9217 appears to be wrong
>
> <https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-messages/lock-semantics>
> lists:
>
> > **CS9217**: *A lock statement on a value of type 'System.Threading.Lock' cannot be used in async
> > methods or async lambda expressions.*
>
> In Roslyn, **CS9217 is `ERR_RefLocalAcrossAwait` — "A 'ref' local cannot be preserved across 'await'
> or 'yield' boundary."** I checked `ErrorCode.cs` on `main` and on the C# 13-era release branches
> `release/dev17.11`, `release/dev17.12` and `release/dev17.13`; all four have
> `ERR_RefLocalAcrossAwait = 9217`, and `CSharpResources.resx` contains no string matching the docs'
> wording. Grepping the whole resx for `System.Threading.Lock` returns only the two CS9216 entries.
>
> Sources: <https://github.com/dotnet/roslyn/blob/main/src/Compilers/CSharp/Portable/Errors/ErrorCode.cs>,
> <https://github.com/dotnet/roslyn/blob/release/dev17.12/src/Compilers/CSharp/Portable/Errors/ErrorCode.cs>,
> <https://github.com/dotnet/roslyn/blob/main/src/Compilers/CSharp/Portable/CSharpResources.resx>
>
> Empirically I could not produce CS9217 from any lock construct (see §5).

### 4. Language version and .NET version

`IDS_FeatureLockObject` is listed under `// C# 13.0 features.` in Roslyn's `MessageID.CheckFeatureAvailability`:

Source: <https://github.com/dotnet/roslyn/blob/main/src/Compilers/CSharp/Portable/Errors/MessageID.cs>

**Confirmed empirically** — building the same file with `<LangVersion>12.0</LangVersion>`:

```
error CS9202: Feature 'Lock object' is not available in C# 12.0. Please use language version 13.0 or greater.
```

Note the interaction: with C# 12, `lock (someLock)` is an **error**, not a silent Monitor fallback.
But CS9216 still fires on the conversions, because that warning is not language-version gated.

The type ships in `System.Runtime.dll` from **.NET 9** (`net-9.0` is the earliest moniker on the API page).
Microsoft Learn's guidance:

> Starting with .NET 9 and C# 13, lock a dedicated object instance of the `System.Threading.Lock` type
> for best performance.

Source: <https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/lock>

### 5. API surface

From the API reference (<https://learn.microsoft.com/en-us/dotnet/api/system.threading.lock>) and
`Lock.cs`:

| Member | Notes |
|---|---|
| `Lock()` | Parameterless ctor. |
| `Scope EnterScope()` | Enters; returns a `ref struct Lock.Scope` with `Dispose()`. What `lock` lowers to. |
| `void Enter()` | Enters, waiting indefinitely. Pair with `try/finally { Exit(); }`. |
| `bool TryEnter()` | Non-blocking. |
| `bool TryEnter(int millisecondsTimeout)` | |
| `bool TryEnter(TimeSpan timeout)` | |
| `void Exit()` | Throws `SynchronizationLockException` if the caller doesn't hold it. |
| `bool IsHeldByCurrentThread` | Property. |

Only `EnterScope` + `Scope.Dispose` are required by the compiler's shape check; the speclet notes the
shape "might not be fully checked (e.g., there will be no errors nor warnings if the `Lock` type is not
`sealed`)".

There is **no `Wait`/`Pulse`** on `Lock` — condition-variable patterns still need `Monitor` (or another
primitive). Worth saying out loud, because it's the one thing that blocks a mechanical `object` → `Lock`
migration.

### 6. Reentrancy

Yes, reentrant, with a recursion count. Docs:

> A thread can enter a lock multiple times before exiting it, such as recursively.
>
> **Note:** A thread that enters a lock, including multiple times such as recursively, must exit the lock
> the same number of times to fully exit the lock and allow other threads to enter the lock. **If a thread
> exits while holding a `Lock`, the behavior of the `Lock` becomes undefined.**

Source: <https://learn.microsoft.com/en-us/dotnet/api/system.threading.lock>

`Lock.cs` carries a `private uint _recursionCount` field and `Enter`/`EnterScope`/`TryEnter` all document
`LockRecursionException` when "the lock has reached the limit of recursive enters. The limit is
implementation-defined". Confirmed empirically (nested `lock (l) { lock (l) { ... } }` works).

Source: <https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Threading/Lock.cs>

### 7. `await` inside — and the async-method nuance most people get wrong

Docs:

> When the lock is being entered and exited in a C# `async` method, ensure that there is no `await`
> between the enter and exit. Locks are held by threads and the code following an `await` might run on a
> different thread.

Source: <https://learn.microsoft.com/en-us/dotnet/api/system.threading.lock>

**Empirically verified matrix** (SDK 10.0.302, `LangVersion` 13.0):

| Code | Result |
|---|---|
| `async Task A() { lock (l) { } await Task.Yield(); }` | **Compiles.** No diagnostic. |
| `async Task C() { using (l.EnterScope()) { } await Task.Yield(); }` | **Compiles.** No diagnostic. |
| `async Task B() { lock (l) { await Task.Yield(); } }` | `error CS1996: Cannot await in the body of a lock statement` |
| `async Task D() { lock (someObject) { await Task.Yield(); } }` | `error CS1996` (same as always) |

So: `lock` on a `Lock` **is** allowed inside an `async` method as long as no `await` appears in the body.
The speclet's "Alternatives" section predicted the opposite —

> Currently, since `lock` is lowered to `using` with a `ref struct` as the resource, this results in a
> compile-time error. The workaround is to extract the `lock` into a separate non-`async` method.

— but C# 13 also shipped `ref struct` locals in async methods (permitted as long as they don't cross an
`await`), which appears to have resolved this. The blocking diagnostic you actually hit is the plain old
**CS1996**, which applies equally to `object` locks.

Source (speclet): <https://github.com/dotnet/csharplang/blob/main/proposals/csharp-13.0/lock-object.md>

### 8. The performance claim — handle with care

This is the weakest-sourced part of the whole story.

- The **API proposal** (dotnet/runtime#34812) makes only a *qualitative* argument: "Locking on any class
  has overhead from the dual role of the syncblock as both lock field and hashcode et al.", motivating
  "a simpler and faster lock as well as be less ambiguous on type and purpose in source code". No
  benchmark numbers. Source: <https://github.com/dotnet/runtime/issues/34812>
- The **implementation PR** (dotnet/runtime#87672, kouvel) does post throughput/CPU tables, but they are
  primarily about **NativeAOT**, where this `Lock` *replaced* the previous `Lock` implementation, and
  about an adaptive-spin strategy that reduces CPU burned in spin-waiting. It is not a general
  "`Lock` beats `Monitor` on CoreCLR by X%" claim. Source: <https://github.com/dotnet/runtime/pull/87672>
- **"Performance Improvements in .NET 9" (Stephen Toub) does not mention `System.Threading.Lock` at all.**
  I fetched the article and searched it. Source: <https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-9/>
- **"What's new in .NET 9 libraries" also does not mention it.** Source: <https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-9/libraries>
- The frequently-quoted **"~25% faster"** figure traces to the README/benchmarks of the third-party
  **`Backport.System.Threading.Lock`** package, *not* to Microsoft. **Secondary source, community-run
  benchmark:** <https://github.com/MarkCiliaVincenti/Backport.System.Threading.Lock>

The only first-party *statement* of a performance benefit is the docs guidance ("lock a dedicated object
instance of the `System.Threading.Lock` type for best performance") and the `Lock` API remark that the
`EnterScope`/`lock` patterns "might also have performance benefits over using `Enter`/`TryEnter` and `Exit`".

My own quick microbenchmark (6 threads, 1.5 s, Release, WSL2 — **not** a rigorous benchmark, no
BenchmarkDotNet, single run) showed `Monitor` and `Lock` within noise of each other under heavy
contention, with `SemaphoreSlim` ~30% behind both:

```
Monitor/lock     total=  370,736   per-thread spread max/min = 1.8x
Threading.Lock   total=  378,300   per-thread spread max/min = 1.1x
SemaphoreSlim    total=  273,879   per-thread spread max/min = 1.3x
```

Treat that as "don't promise a big number on stage", not as a measurement.

### 9. Fairness (relevant to Q6 too)

`Lock` is explicitly **not** a fair/FIFO lock. The source comment is unambiguous that a thread that
never queued can take the lock ahead of queued waiters:

```
// Spinning helps to reduce waiter starvation. Since other non-waiter threads can take the lock while
// there are waiters (see State.TryLock()), once a waiter wakes it will be able to better compete with
// other spinners for the lock.
```

Source: <https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Threading/Lock.cs>

---

## Sources

| Source | What it is |
|---|---|
| <https://github.com/dotnet/csharplang/blob/main/proposals/csharp-13.0/lock-object.md> | **Language spec / proposal** — the `Lock` object speclet. Authoritative for the lowering, the warning rule, and the design alternatives. |
| <https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/lock> | **Official doc** — C# `lock` statement reference; both lowerings written out. |
| <https://learn.microsoft.com/en-us/dotnet/api/system.threading.lock> | **Official doc** — `System.Threading.Lock` API reference and remarks. |
| <https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-messages/lock-semantics> | **Official doc** — lock diagnostics page. Contains the CS9217 error noted above. |
| <https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Threading/Lock.cs> | **Source code** — the runtime implementation (`EnterScope`, `Scope`, `_recursionCount`, barging comment). |
| <https://github.com/dotnet/roslyn/blob/main/src/Compilers/CSharp/Portable/Errors/ErrorCode.cs> | **Source code** — Roslyn diagnostic numbering (`WRN_ConvertingLock = 9216`, `ERR_RefLocalAcrossAwait = 9217`). |
| <https://github.com/dotnet/roslyn/blob/main/src/Compilers/CSharp/Portable/CSharpResources.resx> | **Source code** — exact diagnostic message strings. |
| <https://github.com/dotnet/roslyn/blob/main/src/Compilers/CSharp/Portable/Errors/MessageID.cs> | **Source code** — `IDS_FeatureLockObject` gated to C# 13.0. |
| <https://github.com/dotnet/runtime/issues/34812> | **API proposal** — original `System.Threading.Lock` proposal + approved surface. |
| <https://github.com/dotnet/runtime/pull/87672> | **Implementation PR** — kouvel's `Lock` implementation, adaptive spin, NativeAOT-focused benchmarks. |
| <https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-9/> | **Official Microsoft blog** — checked; contains no mention of `System.Threading.Lock`. |
| <https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-9/libraries> | **Official doc** — checked; contains no mention of `System.Threading.Lock`. |
| <https://github.com/MarkCiliaVincenti/Backport.System.Threading.Lock> | **Secondary / third-party** — origin of the widely-repeated "~25% faster" number. Community benchmark, not Microsoft. |

---

## Talk-ready points

- "`lock` is now two different code generators wearing one keyword. If the compiler can see the static
  type is `System.Threading.Lock`, it emits `using (x.EnterScope())` — a `ref struct` disposed in a
  `finally`. Anything else — `object`, `T`, an interface — and you get the twenty-year-old
  `Monitor.Enter`/`Monitor.Exit` in a `try`/`finally`. The keyword is the same; the lock is not."

- "Here's the failure mode nobody expects. Take one `Lock` instance. Hold it through an `object`-typed
  variable on thread A, so you go down the `Monitor` path. Now on thread B call `TryEnter()` on the same
  instance. It returns **true**. Two threads, one 'lock' object, zero mutual exclusion — because a `Lock`
  instance carries a lock *and* a sync block, and you just used both."

- "The compiler does warn: **CS9216**, *'A value of type System.Threading.Lock converted to a different
  type will use likely unintended monitor-based locking in lock statement.'* But notice where it fires —
  on the **conversion**, not on the `lock`. `object o = myLock;` warns. The `lock (o)` five lines later
  is silent. So if the conversion happens in another file, or in a `List<object>`, or through a generic
  method, the warning is gone and the bug is still there."

- "Careful with the docs on this one. Microsoft Learn's lock-diagnostics page says CS9217 is 'a lock
  statement on a System.Threading.Lock cannot be used in async methods'. In Roslyn, CS9217 is actually
  `ERR_RefLocalAcrossAwait`, and I checked four release branches. What you really get is plain old
  **CS1996, 'Cannot await in the body of a lock statement'** — and `lock` on a `Lock` inside an `async`
  method compiles perfectly fine as long as there's no `await` in the body. I verified all of that with
  the compiler."

- "You need **C# 13 and .NET 9**. On `LangVersion 12` you don't silently fall back to `Monitor` — you get
  a hard error, CS9202, 'Feature Lock object is not available in C# 12.0.' That's actually the safe
  outcome."

- "It's reentrant, it has `Enter`/`TryEnter`/`Exit`/`IsHeldByCurrentThread`, and it is explicitly **not**
  fair — the source comment says non-waiter threads can take the lock while there are waiters. What it
  does *not* have is `Wait`/`Pulse`. So if you're using `Monitor.Wait` as a condition variable, `Lock` is
  not a drop-in replacement."

- "On the performance claim: be honest. The '25% faster' number everyone quotes comes from a third-party
  backport library's README. Stephen Toub's .NET 9 performance post doesn't mention the type at all,
  neither does the What's New doc, and the API proposal only argues qualitatively that you avoid syncblock
  overhead. Use `Lock` because it's unambiguous and type-safe, not because you've been promised a number."

---

## Unverified / open

- **The exact IL.** I verified the lowering behaviourally (`Monitor.IsEntered` is `false` inside
  `lock (lockTypedVar)`, and cross-mechanism exclusion fails) rather than by decompiling. No IL
  disassembler was available in this environment. The spec text is explicit enough that I'm confident,
  but I did not read the emitted IL byte-for-byte.
- **Whether the Learn CS9217 text was ever correct.** It is possible an early C# 13 preview emitted a
  dedicated lock-in-async error that was later removed once `ref struct`s were allowed in async methods.
  I checked `release/dev17.11` onwards and found nothing; I did not search preview branches or Roslyn's
  full git history. Either way it does not describe any shipped compiler I could test.
- **Whether CoreCLR's `Monitor` itself is implemented on top of the managed `Lock`** in .NET 9/10 (it is
  in NativeAOT). I did not confirm this either way, and it matters for how meaningful a
  `Monitor`-vs-`Lock` benchmark is. Flagging rather than guessing.
- **All empirical results were produced on .NET 10 (SDK 10.0.302 / runtime 10.0.10), not .NET 9.** The
  language feature, diagnostics and API are the same, but I did not re-run against a .NET 9 runtime.
- **The `Lock` docs mention STA/`SynchronizationContext` interactions** ("On Windows STA threads, waits
  for locks allow message pumping") — I did not investigate or test this.
