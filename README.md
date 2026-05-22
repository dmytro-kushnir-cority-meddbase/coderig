# Runtime Intelligence Graph

Working repository for a CLI-first .NET code-mining tool.

The project aims to index .NET solutions into immutable SQLite runs, then expose
entrypoint callgraphs annotated with interesting external effects such as HTTP,
EF Core, Redis, and loop/parallel execution contexts.

The current product direction is captured in [docs/mvp-spec.md](docs/mvp-spec.md).
Shared project vocabulary lives in
[docs/ubiquitous-language.md](docs/ubiquitous-language.md).
Current implementation handover notes live in
[docs/handover.md](docs/handover.md).

## Implementation Workflow

Default to contract-first TDD.

For each behavior slice:

1. Add or extend a playground fixture.
2. Hand-author the expected semantic output.
3. Run the test and see it fail.
4. Implement the smallest useful miner/rule/projection change.
5. Make the test green.
6. Do an explicit refactor pass.
7. Re-run relevant tests.
8. Commit the slice.

Tests should protect semantic contracts, not implementation details. Prefer
expected observations, facts, effects, callgraph edges, and CLI output over
tests that mirror internal algorithms.

Do not generate expected results from the same code path being tested. Expected
fixtures should be hand-authored and normalized for unstable values such as
absolute paths, timestamps, run IDs, generated IDs, and line endings.

Use short spikes for unfamiliar Roslyn/MSBuild behavior, but either delete spike
code or turn the learning into a failing fixture test before productizing it.

## Progress Tracking

Track progress in three layers:

1. Milestone checklist in [docs/mvp-spec.md](docs/mvp-spec.md).
2. Slice checklist in [docs/progress.md](docs/progress.md) or an issue tracker.
3. Git commits that each correspond to one green tested behavior slice.

Recommended slice template:

```text
Slice: HttpClient absolute URL effect
Phase: 4
Status: red | green | refactor | verified | committed

Contract:
  - playground code contains HttpClient.GetAsync("https://billing.test/invoices")
  - expected effect is http GET billing.test /invoices
  - confidence=high basis=compilation+profile

Verification:
  - test name or command
  - commit hash when done
```

Keep the current slice small enough that a fresh-context agent can understand
the failing contract, make it green, refactor, and commit without rediscovering
the whole project.
