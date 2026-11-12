# History storage v3

DataManager.Core and ImageManager.Core reference this one assembly. They share compressed payloads when hosted by Data Manager, and preserve their own domain validation and restore routines.

## Storage and compatibility

- Existing `manifest.jsonl`, image `history.jsonl`, snapshots and backups remain intact. New writes do not rewrite the old journals.
- Data events live under `数据/history-v3/ledger`; image events live under the image project's `History/history-v3/ledger`.
- Integrated payloads live under the executable-owned project storage root's `history-v3/blobs`. Standalone Image Manager uses its own backup container.
- A `BlobReference` contains the SHA-256 of original bytes, original length and codec. `br-v1` uses Brotli Fastest; `raw-v1` is selected when compression does not reduce length. Both are verified while reading. Existing representations are reused by hash.
- The ledger has `events-NNNNNNNN.jsonl` segments. Each complete event carries a sequence, preceding checksum and its own checksum. Writes flush before returning. A nonterminated final line is retained and a subsequent append starts a new segment. A damaged complete line stops recovery.
- `storage-format.json` identifies v3 storage. Old executables cannot interpret new events or status overlays; upgraded history is restored with the newer manager.
- V3 is a **storage DTO version**. Data domain entries still use schema `2.0`/`2.1`; decoding v3 does not weaken Profile, Variant, LayoutRevision, existence, length or SHA checks.
- Legacy listing uses a rebuildable metadata/offset index, without base64-decoding payloads. Restore loads the selected old operation. Existing malformed legacy lines remain untouched and cannot gain replay authority.

## Transaction boundary

1. Acquire a named project mutex, shared by data, image and migration writers.
2. Persist and verify payloads for every possible target, including optional image/save companions.
3. Flush `Prepare` before the first target replacement.
4. Keep existing candidate validation, dependency checks, atomic file replacement and semantic reread.
5. Flush `Commit` after every successful contribution is verified. Only actual contributions enter the successful operation.
6. On failure, restore the entire known group, then append `Abort`; failed recovery records `Conflict` and retains all materials.

Undo/redo use the same prepare/commit boundary and append status transitions. Restart recovery validates the entire pending group's current before/after state and both snapshot directions before modifying any member. Any unknown external content prevents automatic replacement. MainForm recovers image transactions before loading range/resource editors and refreshes workspace data after recovery.

Metadata indexes are disposable acceleration, never recovery authority. Content blobs and original journals are not automatically pruned. No retention or cleanup policy is implemented by this library.

## Background boundaries

The shared `IHistoryBlobStore`, `IHistoryLedger`, and `IHistoryRepository<TContext,TMetadata,TPayload>` interfaces are used by both production repositories and by their hosts. A history list contains metadata and blob references, never decoded snapshot arrays.

Data exposes `FreezeWrite` → `PrepareWriteAsync` → `AppendPrepared`: freezing copies owned bytes while the UI owns its drafts; the worker only writes immutable blobs; the final ledger append remains ordered with the caller's file transaction. `PrepareRestoreAsync` decodes and verifies one operation on a worker, and `RestorePrepared` rechecks current status/layout/hashes before restoring. Frozen/prepared payload holders are disposable to release their large byte references.

Image exposes owned `FrozenImageMutation` → `CommitFrozenAsync` for independent resource operations, and `HistoryService.RestoreAsync`. These APIs do not capture WinForms controls, `Bitmap`, or live `DataTable` objects. Cancellation is honored before transaction replacement begins; an in-progress transaction completes or rolls back.

The main-window ordinary Save uses `ProjectWorkspace.SaveAsync`: draft preparation and candidate construction remain on the UI context, immutable payload compression runs on a worker, then baseline verification, mutex acquisition, durable Prepare, replacement, semantic reread and draft acceptance resume on the original context. The mutex never crosses an await. Final Commit reuses the prepared blob references rather than recompressing payloads. Input, project switching, shortcuts, repeated save and window closing are blocked for that lifecycle. Synchronous `Save` remains available for migration and compatibility callers without awaiting a UI continuation.

Main-window Data restore prepares one payload on a worker and rechecks the commit on the UI context; independent Image restore runs its file transaction on a worker and refreshes the workspace afterward. Integrated image/save mutation from the picture editor still follows its existing synchronous host coordination. Candidate construction, project file replacement and semantic validation during ordinary Save also remain synchronous; no live workspace or `DataTable` is sent to `Task.Run`.

## Verification

`HistoryStorageV3Tests` covers Brotli/raw exact round trips, hash deduplication and legacy codec reuse, payload corruption, ledger checksum/torn tails, unchanged old journals, lazy payload reading, Data/Image undo and redo, interrupted transactions, external conflicts and project locking. Existing history, creation/deletion, writeback, image/save contribution and migration tests exercise late file and final-ledger failures.

`HistoryAsyncSaveTests` runs a real STA message pump with a 2 MiB incompressible payload. It checks that compression yields the UI, draft participants and durable append return to the original context, repeated save/project switching/window closing are refused during preparation, late ledger failure restores files and retains drafts, synchronous Save still works, and the actual MainForm asynchronous save/undo/redo paths restore input and history state.

`HistoryLedger.ObserveAppends` is scoped synchronous instrumentation at the actual append boundary. Regression tests use it to acquire a real read-shared file handle just before final commit, so they exercise a late persistence failure after target replacement rather than merely rejecting preflight.
