# Q5 — Azure Blob Leases as a Distributed Lock

## Summary

An Azure Blob lease is a server-enforced exclusive write-and-delete lock on a single blob, acquired for **15 to 60
seconds or infinitely (-1)**, renewable, releasable, changeable, and breakable by any authorised caller. The decisive
property — and the reason it is materially different from a Redis lock — is that Azure Storage itself enforces the
lease: once a blob is leased, **every write operation to that blob must carry the lease ID**, and a write with a
missing, stale, or wrong lease ID is rejected by the service with `412 Precondition Failed` or `409 Conflict`. That
makes the lease ID function as a fencing mechanism *for writes to that specific blob* — the resource participates,
which is exactly the property Kleppmann says a lock needs and Redlock lacks. It is **not** a monotonic fencing token
in Kleppmann's sense (it is a GUID checked for equality against the currently-active lease, not an ordering), and it
fences nothing except that blob — a paused lease holder can still send emails, call APIs, and write to a database. It
is what `madelson/DistributedLock`'s `AzureBlobLeaseDistributedLock` is built on.

---

## Findings

### The five lease actions

Source: https://learn.microsoft.com/en-us/rest/api/storageservices/lease-blob

> You can call the `Lease Blob` operation in one of the following modes:
>
> - `Acquire`, to request a new lease.
> - `Renew`, to renew an existing lease.
> - `Change`, to change the ID of an existing lease.
> - `Release`, to free the lease if it's no longer needed, so that another client can immediately acquire a lease
>   against the blob.
> - `Break`, to end the lease, but ensure that another client can't acquire a new lease until the current lease
>   period has expired.

Success status codes: `Acquire` → 201 Created; `Renew`, `Change`, `Release` → 200 OK; `Break` → 202 Accepted.

The request is `PUT https://myaccount.blob.core.windows.net/mycontainer/myblob?comp=lease`.

### Exact allowed durations — CONFIRMED: 15 to 60 seconds, or -1 for infinite

Source: https://learn.microsoft.com/en-us/rest/api/storageservices/lease-blob — opening line:

> The `Lease Blob` operation creates and manages a lock on a blob for write and delete operations. **The lock duration
> can be 15 to 60 seconds, or can be infinite.** In versions prior to 2012-02-12, the lock duration is 60 seconds.

The `x-ms-lease-duration` header, verbatim:

> `x-ms-lease-duration: -1 | n seconds` — Version 2012-02-12 and later. Only allowed and required on an `acquire`
> operation. Specifies the duration of the lease, in seconds, or negative one (-1) for a lease that never expires. **A
> non-infinite lease can be between 15 and 60 seconds.** A lease duration can't be changed by using `renew` or `change`.

Three points that matter and are easy to get wrong:

1. The duration header is **required** on acquire (since version 2012-02-12): "If you try to acquire a lease without
   specifying a lease duration, the service returns `400 Bad Request – Missing required header`."
2. The duration **cannot be changed** by `renew` or `change` — renew resets the clock at the original duration.
3. Re-acquiring with the *same* lease ID on an already-leased blob is allowed and can specify a new duration:
   "If the blob has an active lease, you can only request a new lease by using the active lease ID. You can, however,
   specify a new `x-ms-lease-duration`, including negative one (-1) for a lease that never expires."

The same 15-60/infinite range is repeated in the .NET SDK docs — https://learn.microsoft.com/en-us/dotnet/api/azure.storage.blobs.specialized.blobleaseclient?view=azure-dotnet :

> The `Acquire(TimeSpan, RequestConditions, CancellationToken)` operation acquires a lease on the blob or container.
> The lease `duration` must be between 15 to 60 seconds, or infinite (-1).

And in the conceptual doc — https://learn.microsoft.com/en-us/azure/storage/blobs/concurrency-manage :

> When you acquire the lease, you specify the duration of the lease. A finite lease may be valid from between 15 to 60
> seconds. A lease can also be infinite, which amounts to an exclusive lock. You can renew a finite lease to extend
> it, and you can release the lease when you're finished with it. Azure Storage automatically releases finite leases
> when they expire.

`BlobLeaseClient.InfiniteLeaseDuration` is `public static readonly TimeSpan InfiniteLeaseDuration` — per the SDK
source it is `TimeSpan.FromSeconds(Constants.Blob.Lease.InfiniteLeaseDuration)` where
`public const int InfiniteLeaseDuration = -1;` (with the comment "Lease Duration is set as infinite when passed -1"),
i.e. `TimeSpan.FromSeconds(-1)`.
Sources: https://learn.microsoft.com/en-us/dotnet/api/azure.storage.blobs.specialized.blobleaseclient.infiniteleaseduration?view=azure-dotnet and
https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/storage/Azure.Storage.Common/src/Shared/Constants.cs

### Renew, change, release, break — exact semantics

Source: https://learn.microsoft.com/en-us/rest/api/storageservices/lease-blob (`x-ms-lease-action` header), verbatim:

> `renew`: Renews the lease. You can renew the lease if the lease ID specified on the request matches that associated
> with the blob. Note that the lease can be renewed even if it has expired, as long as the blob hasn't been modified
> or leased again since the expiration of that lease. When you renew a lease, the lease duration clock resets.

> `change`: Version 2012-02-12 and later. Changes the lease ID of an active lease. A `change` must include the current
> lease ID in `x-ms-lease-id`, and a new lease ID in `x-ms-proposed-lease-id`.

> `release`: Releases the lease. You can release the lease if the lease ID specified on the request matches that
> associated with the blob. Releasing the lease allows another client to immediately acquire the lease for the blob,
> as soon as the release is complete.

> `break`: Breaks the lease, if the blob has an active lease. After a lease is broken, it can't be renewed. **Any
> authorized request can break the lease; the request isn't required to specify a matching lease ID.** When a lease is
> broken, the lease break period is allowed to elapse, during which time `break` and `release` are the only lease
> operations you can perform on the blob. When a lease is successfully broken, the response indicates the interval in
> seconds until a new lease can be acquired. A lease that has been broken can also be released, in which case another
> client can immediately acquire the lease on the blob.

**What `breakPeriod` is** (`x-ms-lease-break-period` in REST, `breakPeriod` in the SDK) — verbatim:

> `x-ms-lease-break-period: N` — Optional. Version 2012-02-12 and later. For a `break` operation, this is the proposed
> duration of seconds that the lease should continue before it is broken, **between 0 and 60 seconds**. This break
> period is only used if it's shorter than the time remaining on the lease. If longer, the time remaining on the lease
> is used. A new lease will not be available before the break period has expired, but the lease can be held for longer
> than the break period. If this header doesn't appear with a `break` operation, a fixed-duration lease breaks after
> the remaining lease period elapses, and an infinite lease breaks immediately.

In plain terms: `break` is the "somebody else is stuck, take it away" operation. It does **not** require the current
lease ID, and it does not grant the caller the lease — it starts a countdown (the break period, capped at the lease's
own remaining time) after which the blob becomes acquirable by anyone. It is the escape hatch for an infinite lease
whose holder died. `break` with `period=0` on a leased blob goes straight to `Broken`.

### Lease states

Source: same page.

> - `Available`: The lease is unlocked and can be acquired. Allowed action: `acquire`.
> - `Leased`: The lease is locked. Allowed actions: `acquire` (same lease ID only), `renew`, `change`, `release`, and `break`.
> - `Expired`: The lease duration has expired. Allowed actions: `acquire`, `renew`, `release`, and `break`.
> - `Breaking`: The lease has been broken, but the lease will continue to be locked until the break period has expired. Allowed actions: `release` and `break`.
> - `Broken`: The lease has been broken, and the break period has expired. Allowed actions: `acquire`, `release`, and `break`.

Two operationally important remarks:

> After a lease has expired, the lease ID is maintained by Blob Storage until the blob is modified or leased again. A
> client can attempt to renew or release the lease by using their expired lease ID. If the operation is successful,
> this means that the blob hasn't been changed since the lease ID was last valid.

> If a lease expires rather than being explicitly released, a client might need to wait up to one minute before a new
> lease can be acquired for the blob. However, the client can renew the lease with their lease ID immediately, if the
> blob hasn't been modified.

That first remark is a genuinely useful primitive: a successful `renew` with a lapsed lease ID is *proof* nobody
touched the blob in the interim.

---

## The key question: does the lease ID act as a fencing token?

### Yes — for writes to that blob, and the service enforces it

Source: https://learn.microsoft.com/en-us/rest/api/storageservices/lease-blob, "Remarks". Verbatim:

> A lease on a blob provides exclusive write and delete access to the blob. **To write to a blob with an active lease,
> a client must include the active lease ID with the write request.** The lease is granted for the duration specified
> when the lease is acquired. This duration can be between 15 and 60 seconds, or an infinite duration.

And the explicit operation list — verbatim:

> When a lease is active, the lease ID must be included in the request for any of the following operations:
>
> - Put Blob
> - Set Blob Metadata
> - Set Blob Properties
> - Delete Blob
> - Put Block
> - Put Block List
> - Put Page
> - Append Block
> - Copy Blob (lease ID needed for destination blob)
>
> **If the lease ID isn't included, these operations fail on a leased blob, with `412 – Precondition failed`.**

Reads are *not* fenced by default:

> The following operations succeed on a leased blob, without including the lease ID: Get Blob, Get Blob Metadata, Get
> Blob Properties, Get Block List, Get Page Ranges, List Blobs, Copy Blob (No lease ID needed for source blob.), Lease
> Blob (No lease ID needed for `x-ms-lease-action: break`.)
>
> It's not necessary to include the lease ID for `GET` operations on a blob that has an active lease. However, all
> `GET` operations support a conditional lease parameter, where the operation only proceeds if the lease ID included
> with the request is valid.

Confirmed independently on the `Put Blob` page — https://learn.microsoft.com/en-us/rest/api/storageservices/put-blob :

Request header:
> `x-ms-lease-id:<ID>` — Required if the blob has an active lease. To perform this operation on a blob with an active
> lease, specify the valid lease ID for this header.

Remarks, verbatim:
> If the blob has an active lease, the client must specify a valid lease ID on the request to overwrite the blob. **If
> the client doesn't specify a lease ID or specifies an invalid lease ID, Blob Storage returns status code 412
> (Precondition Failed).** If the client specifies a lease ID but the blob doesn't have an active lease, Blob Storage
> also returns status code 412 (Precondition Failed). If the client specifies a lease ID on a blob that doesn't yet
> exist, Blob Storage returns status code 412 (Precondition Failed) for requests made against version 2013-08-15 and later.

And:
> If an existing blob with an active lease is overwritten by a `Put Blob` operation, the lease persists on the updated
> blob until it expires or is released.

### The outcome table — this is the evidence that a STALE lease ID is rejected

Source: https://learn.microsoft.com/en-us/rest/api/storageservices/lease-blob, "Outcomes of use attempts on blobs by
lease state". `(A)` and `(B)` are lease IDs. Reproduced verbatim:

| Action | Available | Leased (A) | Breaking (A) | Broken (A) | Expired (A) |
|---|---|---|---|---|---|
| Write with (A) | Fails (412) | Leased (A), write succeeds | Breaking (A), write succeeds | Fails (412) | Fails (412) |
| Write with (B) | Fails (412) | Fails (409) | Fails (412) | Fails (412) | Fails (412) |
| Write, no lease specified | Available, write succeeds | Fails (412) | Fails (412) | Available, write succeeds | Available, write succeeds |
| Read with (A) | Fails (412) | Leased (A), read succeeds | Breaking (A), read succeeds | Fails (412) | Fails (412) |
| Read with (B) | Fails (412) | Fails (409) | Fails (409) | Fails (412) | Fails (412) |
| Read, no lease specified | Available, read succeeds | Leased (A), read succeeds | Breaking (A), read succeeds | Broken (A), read succeeds | Expired (A), read succeeds |

Now run Kleppmann's GC-pause scenario against that table. Client 1 holds lease (A) and is paused past expiry:

- **Case 1: the lease expired and Client 2 has since acquired lease (B).** Blob state is `Leased (B)`. Client 1 writes
  with (A) — that is the "Write with (B) on Leased (A)" row by symmetry → **Fails (409)**. Client 1 is fenced.
- **Case 2: the lease expired and nobody re-acquired.** Blob state is `Expired (A)`. Client 1 writes with (A) →
  **Fails (412)**. Client 1 is fenced.
- **Case 3: Client 2 broke the lease.** Blob state is `Broken (A)`. Client 1 writes with (A) → **Fails (412)**. Fenced.

So in every case, a paused client that resumes and writes **with its lease ID** is rejected by the service. This is
resource-side enforcement — exactly the "the storage server takes an active role" property Kleppmann says fencing
requires.

### But it is not a fencing token in Kleppmann's precise sense, and there are real caveats

1. **It is not monotonic.** Kleppmann's fencing token is "a number that increases... every time a client acquires the
   lock" (https://martin.kleppmann.com/2016/02/08/how-to-do-distributed-locking.html), and the storage server
   "remembers that it has already processed a write with a higher token number". A lease ID is a GUID checked for
   *equality* against the currently-active lease. There is no ordering, no memory of past tokens, and no way to say
   "this write is from an older lock generation". The safety property comes from Azure Storage holding the single
   authoritative lease state, not from an ordering argument. It achieves the same *effect* for this blob — closer in
   spirit to antirez's "unique token + check-and-set" than to Kleppmann's monotonic counter.
2. **The client must actually send the lease ID.** Look at the "Write, no lease specified" row: on `Expired (A)` and
   `Broken (A)` a write with **no** lease ID **succeeds**. So if a paused Client 1 resumes and writes through a code
   path that forgets to attach `BlobRequestConditions.LeaseId`, it is not fenced at all. The enforcement is
   conditional on the client's own discipline, unlike a monotonic token embedded in every write by construction.
3. **It fences only that blob.** The lease protects `Put Blob`, `Put Block`, `Set Blob Metadata`, `Delete Blob` etc.
   **on the leased blob**. If the work under the lock is "write to SQL Server", "call the Land Registry API", "send
   the client an email", or "write to a *different* blob", the lease fences none of it. A paused holder can still do
   all of that. The fencing guarantee is exactly as wide as the blob and no wider.
4. **Container leases are weaker still.** Per
   https://learn.microsoft.com/en-us/azure/storage/blobs/concurrency-manage : "For containers, however, the exclusive
   lock is enforced only on delete operations. To delete a container with an active lease, a client must include the
   active lease ID with the delete request. All other container operations succeed on a leased container without the
   lease ID." Also, from the Lease Blob remarks: "All container operations are permitted on a container that includes
   blobs with an active lease, including Delete Container. Therefore, a container can be deleted even if blobs within
   it have active leases."
5. **Reads are shared by default.** Per the concurrency doc: "When a lease exists, Azure Storage enforces exclusive
   access to write operations for the lease holder. However, ensuring exclusivity for read operations requires the
   developer to make sure that all client applications use a lease ID and that only one client at a time has a valid
   lease ID. Read operations that don't include a lease ID result in shared reads."

**Net answer.** For the specific case "the protected resource IS the blob", an Azure Blob lease is materially safer
than a Redis lock, because the resource itself refuses stale writers, and because there is one authoritative service
clock rather than N nodes whose drift you must bound. For the general case "the protected resource is anything else",
it is a lock with a TTL and inherits every limitation in `03-redlock-debate.md`.

### The other Azure mechanism: ETag / If-Match optimistic concurrency

Source: https://learn.microsoft.com/en-us/azure/storage/blobs/concurrency-manage

> Azure Storage assigns an identifier to every object stored. This identifier is updated every time a write operation
> is performed on an object. The identifier is returned to the client as part of an HTTP GET response in the ETag
> header defined by the HTTP protocol.
>
> A client that is performing an update can send the original ETag together with a conditional header to ensure that
> an update only occurs if a certain condition has been met. For example, if the **If-Match** header is specified,
> Azure Storage verifies that the value of the ETag specified in the update request is the same as the ETag for the
> object being updated.

The four-step protocol, verbatim:

> 1. Retrieve a blob from Azure Storage. The response includes an HTTP ETag Header value that identifies the current version of the object.
> 2. When you update the blob, include the ETag value you received in step 1 in the **If-Match** conditional header of the write request. Azure Storage compares the ETag value in the request with the current ETag value of the blob.
> 3. If the blob's current ETag value differs from the ETag value specified in the **If-Match** conditional header provided on the request, then Azure Storage returns HTTP status code 412 (Precondition Failed). This error indicates to the client that another process has updated the blob since the client first retrieved it. The client should fetch the blob again to get the updated content and properties.
> 4. If the current ETag value of the blob is the same version as the ETag in the **If-Match** conditional header in the request, Azure Storage performs the requested operation and updates the current ETag value of the blob.

In C#, that is `new BlobUploadOptions { Conditions = new BlobRequestConditions { IfMatch = originalETag } }`, and the
failure surfaces as `RequestFailedException` with `Status == 412`.

Microsoft's framing of the three strategies, verbatim:

> Azure Storage supports all three strategies [optimistic, pessimistic, last writer wins], although it's distinctive
> in its ability to provide full support for optimistic and pessimistic concurrency. [...] You can opt to use either
> optimistic or pessimistic concurrency models to manage access to blobs and containers. **If you don't explicitly
> specify a strategy, then by default the last writer wins.**

Note that Microsoft does **not** declare a winner between the two; it also warns up front: "The Azure Storage client
libraries don't support concurrent writes to the same blob, with the exception of append blobs if the write order
doesn't matter. If your app requires multiple processes writing to the same blob, you should implement a strategy for
concurrency control."

The practical contrast for a talk: ETag/`If-Match` is a compare-and-swap on the blob's version — no lock, no TTL, no
renewal, and it detects conflict at write time rather than preventing it. It is antirez's "check and set" made native.
A lease *prevents* the concurrent write; an ETag *detects* it. They compose: you can hold a lease and still send
`If-Match`.

### Does `madelson/DistributedLock`'s Azure provider use this? — Yes

Source: https://github.com/madelson/DistributedLock/blob/master/docs/DistributedLock.Azure.md

> The DistributedLock.Azure package offers distributed locks based on [Azure blob leases]. For example:
>
> ```csharp
> var container = new BlobContainerClient(myAzureConnectionString, "my-locking-container-name");
> var @lock = new AzureBlobLeaseDistributedLock(container, "MyLockName");
> await using (var handle = await @lock.TryAcquireAsync())
> {
>   if (handle != null) { /* I have the lock */ }
> }
> ```

Implementation notes, verbatim:

> `AzureBlobLeaseDistributedLock`s can be constructed either from a `BlobContainerClient` and a name, which will cause
> it to lease a blob in the provided container with a name based on the provided name. If you know exactly which blob
> you'd like to lease, another constructor lets you pass a `BlobBaseClient` instead.

> **Because of how Azure leases work, the acquire operation cannot truly block.** If waiting to acquire a lock that is
> not available, the implementation will periodically sleep and retry until the lease can be taken or the acquire
> timeout elapses. Because of this, these locks are maximally efficient when using `TryAcquire` semantics with a
> timeout of zero.

> Blob leases in Azure have built-in expirations. However **while an `AzureBlobLeaseDistributedLock` is held it will
> periodically renew the lease in the background.** Therefore, it is generally safe to ignore the problem of lease duration.

Tuning options exposed: `Duration` (the requested lease duration), `RenewalCadence` (background auto-renew interval),
and `BusyWaitSleepTime` (a randomised sleep range between acquisition attempts — the same "random delay to
desynchronise contenders" idea redis.io prescribes for Redlock).

Two things worth flagging for a talk:

- The library leases a **sentinel blob named after the lock**, not your actual data blob (unless you pass a
  `BlobBaseClient`). In that default mode the fencing property described above **does not apply to your real
  resource** — you get a mutual-exclusion lock, not a fenced one. To get the fencing benefit you must lease the very
  blob you are protecting *and* pass the lease ID on every write.
- Background auto-renewal reduces, but does not eliminate, the overrun window: a GC pause long enough to miss the
  renewal cadence still loses the lease, and the process will not know until its next call fails.

---

## Talk-ready points

- "Azure Blob leases: 15 to 60 seconds, or -1 for infinite. That's the whole range. Not 10, not 90. And on acquire the
  duration header is required — omit it and you get `400 Bad Request – Missing required header`."
- "Renew resets the clock but cannot change the duration. Change swaps the lease ID. Release hands it back
  immediately. And Break is the escape hatch — **any authorised caller can break a lease without knowing the lease
  ID**, which is how you recover a blob whose infinite-lease holder died."
- "`breakPeriod` is the grace window on a break: 0 to 60 seconds, and it's capped at whatever time the lease had
  left. Break with period 0 and the blob is acquirable immediately."
- "Here's the part that actually matters for our talk. Azure Storage says: 'To write to a blob with an active lease, a
  client must include the active lease ID with the write request.' Put Blob, Put Block, Put Block List, Set Metadata,
  Delete — all of them. No lease ID, and you get `412 Precondition Failed`."
- "Run Kleppmann's GC-pause scenario against Microsoft's own outcome table. Client 1's lease expires, Client 2 takes
  lease B, Client 1 wakes up and writes with lease A. Result: **409 Conflict**. If nobody re-took the lease, Client 1
  gets **412**. Either way the paused client is rejected *by the service*. That's the resource taking an active
  role — the thing Kleppmann says Redlock can't give you."
- "So is the lease ID a fencing token? Honest answer: functionally yes for that blob, technically no. It's a GUID
  checked for equality against the current lease, not a monotonically increasing number with ordering. It's closer to
  antirez's 'unique token plus check-and-set' than to Kleppmann's counter. But the effect is what you want."
- "Two caveats that will bite you. One: the client still has to *send* the lease ID. Look at the table — 'write, no
  lease specified' on an expired lease **succeeds**. Forget `BlobRequestConditions.LeaseId` in one code path and the
  fencing evaporates. Two: it fences the blob and nothing else. Your paused process can still charge a credit card,
  send an email, and write to SQL Server. Blob-scoped fencing is not workflow-scoped fencing."
- "Container leases are weaker again — the exclusive lock is enforced *only on delete*. Every other container
  operation succeeds without the lease ID."
- "The other Azure mechanism is ETag with `If-Match`. No lock, no TTL: read the blob, keep the ETag, send it back on
  write, get 412 if someone changed it underneath you. A lease *prevents* the conflicting write; an ETag *detects*
  it. And Microsoft's warning: if you pick neither, the default is last-writer-wins."
- "`madelson/DistributedLock` has an Azure provider — `AzureBlobLeaseDistributedLock` — and yes, it's blob leases
  under the hood, with background auto-renewal and randomised busy-wait polling. Two things to know: acquire can't
  truly block, so use `TryAcquire`; and by default it leases a *sentinel* blob named after the lock, not your data.
  In that mode you get mutual exclusion but you do **not** get the fencing property — for that you must lease the
  actual blob you're protecting and pass the lease ID on every write."
- The one-line comparison for a slide: "Redis lock = the lock tells you you're safe. Azure blob lease = the storage
  service refuses to let you be unsafe — but only about that one blob."

---

## Sources

**Primary — Microsoft Learn**

- https://learn.microsoft.com/en-us/rest/api/storageservices/lease-blob — `Lease Blob` REST reference. The five
  actions, `x-ms-lease-duration` (15-60 or -1), `x-ms-lease-break-period` (0-60), the lease state machine, the list of
  operations requiring the lease ID, the 412 rule, and the outcome-by-lease-state tables.
- https://learn.microsoft.com/en-us/rest/api/storageservices/put-blob — `Put Blob` REST reference. Independently
  confirms `x-ms-lease-id` is "Required if the blob has an active lease" and that a missing or invalid lease ID
  returns 412.
- https://learn.microsoft.com/en-us/azure/storage/blobs/concurrency-manage — "Manage concurrency in Blob Storage".
  Optimistic (ETag/`If-Match`) vs pessimistic (lease) concurrency, the C# examples for both, the 15-60/infinite
  restatement, the container-lease-only-fences-delete limitation, and the "by default the last writer wins" warning.
- https://learn.microsoft.com/en-us/dotnet/api/azure.storage.blobs.specialized.blobleaseclient?view=azure-dotnet —
  `BlobLeaseClient` .NET API reference. Constructors, `LeaseId`, `InfiniteLeaseDuration`, and
  Acquire/Renew/Change/Release/Break (+ Async) with the 15-60/-1 constraint restated per method.
- https://learn.microsoft.com/en-us/dotnet/api/azure.storage.blobs.specialized.blobleaseclient.infiniteleaseduration?view=azure-dotnet
  — the `InfiniteLeaseDuration` field page (declaration only; the numeric value is not printed here).

**Primary — SDK source (for the value the docs page omits)**

- https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/storage/Azure.Storage.Blobs/src/BlobLeaseClient.cs —
  `public static readonly TimeSpan InfiniteLeaseDuration = TimeSpan.FromSeconds(Constants.Blob.Lease.InfiniteLeaseDuration);`
- https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/storage/Azure.Storage.Common/src/Shared/Constants.cs —
  `public const int InfiniteLeaseDuration = -1;`

**Primary — library documentation (third-party project, but its own docs)**

- https://github.com/madelson/DistributedLock/blob/master/docs/DistributedLock.Azure.md — `DistributedLock.Azure`
  documentation: `AzureBlobLeaseDistributedLock`, the non-blocking acquire, background renewal, and the `Duration` /
  `RenewalCadence` / `BusyWaitSleepTime` options.
- https://github.com/madelson/DistributedLock/blob/master/README.md — package index and changelog confirming
  DistributedLock.Azure is the blob-lease provider.

**Cross-referenced (secondary for this file)**

- https://martin.kleppmann.com/2016/02/08/how-to-do-distributed-locking.html — used only for the definition of a
  fencing token against which the lease ID is compared. See `03-redlock-debate.md`.

---

## Unverified / open

- **Which HTTP error *code strings* accompany the 412/409 responses** (e.g. `LeaseIdMissing`,
  `LeaseIdMismatchWithBlobOperation`, `LeaseNotPresentWithBlobOperation`). The Lease Blob and Put Blob pages give the
  status codes but not the `<Code>` values; those live on the "Blob service error codes" page, which I did not fetch.
  Do not quote specific error-code strings without checking.
- **`Set Blob Metadata` / `Put Block` per-page confirmation.** Both are named in the Lease Blob remarks list of
  operations requiring the lease ID, and `Put Blob` was independently confirmed on its own page. I did not open the
  `Set Blob Metadata` or `Put Block` pages individually.
- **`Lease Container` REST page not fetched directly.** The container-lease semantics quoted here come from
  `concurrency-manage` and the `Lease Blob` remarks. The 15-60/infinite range for *container* leases is stated by the
  .NET `BlobLeaseClient` docs (which cover both) but not verified against the `Lease Container` REST page itself.
- **Whether Azure Storage's lease expiry is subject to any documented clock-skew allowance.** Microsoft does not
  discuss it in the pages read; the single-service-clock argument in this file is an inference from the architecture
  (one authoritative service holds lease state), not a quoted guarantee. Do not present it as a Microsoft claim.
- **Exactly how `DistributedLock.Azure` names its sentinel blob**, and whether it writes any content to it. Stated in
  the docs only as "a blob in the provided container with a name based on the provided name". Check the source before
  asserting specifics.
- **Whether the team currently uses blob leases or `DistributedLock.Azure`.** Not investigated in this
  pass.
