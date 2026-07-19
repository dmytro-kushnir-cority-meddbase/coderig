# Allocation effect detector, with value-copy follow-up

**Status:** PROGRESS — core allocation facts, compiler/string lowering, cardinality, and conservative x64
shallow-size estimates implemented and AngleSharp-core validated 2026-07-19; value-copy facts and measured
library summaries remain. · **Family:** performance analysis
· **Primary evaluation target:** AngleSharp core

**Related:** [[redundant-graph-index-rebuild-per-query]] + [[warm-graph-across-queries]].

## Goal

Make allocation visible in rig's existing effect graph so `derive`, `tree`, `reaches`, and `impact` can answer:

- where can this path allocate?
- which allocation sites are inside loops or guarded branches?
- what allocated type and approximate shallow size can be established statically?

No dedicated allocation CLI is needed. Allocation should use the existing effect filters and projections,
but it is **not a user-declared effect rule**.

## Core-owned machinery

Allocation is a language/runtime property established by the compiler model. It therefore belongs beside
symbol/reference/dispatch extraction as a dedicated core fact family:

```text
Roslyn allocation semantics -> allocation facts -> core allocation deriver -> DerivedEffect(alloc:*)
```

- `rig.rules.json` must not define what counts as an allocation.
- `builtin-rules.json` must not contain a framework/API catalogue pretending to be allocation semantics.
- A repository may filter `alloc` in a query, but cannot redefine object/array/boxing semantics.
- Core-derived allocation effects union with rule-derived effects before the existing filtering, graph,
  hazard, cache, and rendering paths.
- Detailed allocation evidence remains on the core fact even when projected into the common `DerivedEffect`
  shape.

This keeps policy effects extensible while giving language-level observations a stable, trusted meaning.

## Detector boundary

Detect allocations from C#/Roslyn semantics, not from a curated LINQ method list. LINQ is only one possible
source and should not define the feature. Allocation extraction is unconditional core indexing work; rules
are neither consulted nor required.

Start with high-confidence sites:

- creation of reference-type objects;
- array creation, including implicit array creation;
- boxing conversions.

Likely later coverage:

- closures and delegate objects;
- iterator and async state machines;
- anonymous objects and implicit string construction where not already covered by object creation;
- known framework APIs that allocate despite having no allocation syntax at the call site.

The later group should be added from measured misses, not as an upfront API catalogue.

## Effect shape

Use provider `alloc` with a small operation vocabulary such as:

- `alloc:object`
- `alloc:array`
- `alloc:boxing`
- later, `alloc:compiler_generated` or a more precise operation if measurements justify it

The resource is the allocated static type where it is known. Preserve the enclosing method, source location,
loop context, and guard context so existing graph and observation machinery remains useful.

An allocation effect means “this source/semantic site can allocate.” It does not mean the site executed, the
allocation survived JIT optimization, or the object was retained.

## Size estimates

Size is useful evidence, but false precision would make the detector worse.

- Report **shallow bytes per occurrence**, never retained graph size or total workload bytes.
- Distinguish `known`, `estimated`, and `unknown`.
- For arrays, include the object/array overhead and element width only when the element type and constant
  length make an estimate defensible; otherwise report the per-element width or unknown length.
- For objects, an x64 shallow estimate may use instance-field layout plus object overhead, with its runtime
  and pointer-size assumptions disclosed.
- For boxing, estimate the box overhead plus the boxed value's shallow size.
- Unknown is a valid result for open generics, error types, runtime-dependent layout, or dynamic lengths.

Do not multiply a shallow estimate by a guessed execution count. Loop/nested-loop/must-run/reach-fan-in are
separate structural signals.

## Struct-copy companion

Struct copying is closely related optimization evidence but is not allocation and should not be emitted as an
`alloc` effect.

A later companion detector should cover:

- by-value arguments and returns;
- boxing, joined to the corresponding allocation effect;
- compiler defensive copies from readonly receivers;
- foreach/property/indexer/assignment copies where Roslyn can identify a source-level candidate;
- shallow value-type size estimates using the same estimator vocabulary.

The AngleSharp streaming API provides a useful regression target: large `ref struct` callback values are
deliberately passed with `in`. The copy detector should show the by-value equivalent and leave the current
`in` sites alone. Machine-code copy elision still requires disassembly to confirm.

## First slice

**Implemented and validated 2026-07-19.**

Implement only:

1. reference-type object creation;
2. array creation;
3. boxing;
4. allocated type and best-effort shallow-size evidence;
5. existing loop and guard context on those sites;
6. a dedicated persisted allocation-fact family;
7. a core deriver that unions `alloc` effects with rule-derived effects before existing filters run.

Do not add a new CLI command, JSON allocation rules, LINQ-specific matching, allocation hazards, hot-path
scores, web UI, or struct-copy facts in this slice.

This is an extraction change and therefore requires re-indexing the evaluation target.

### First-slice result

The core-owned path is end-to-end: Roslyn extraction -> dedicated `allocation_facts` table -> pure core
deriver -> the shared effect stream. Whole-store `derive`, bounded `tree`/`reaches`/`path`, and both `impact`
sides consume the same facts. No allocation rule or dedicated CLI was added.

AngleSharp.Core project calibration (705 files, three pre-existing degraded-compilation diagnostics in
`HtmlEntityProvider`) produced **861** allocation facts:

- 759 `alloc:object`;
- 16 `alloc:array`;
- 86 `alloc:boxing`;
- 44 sites carried the existing `looped_effect` observation.

`tree HtmlParser.ParseFragment --only alloc --view effects` surfaced 17 reachable effectful methods. Useful
early candidates include parser-builder/tokenizer objects and `NodeFlags` boxing at `Enum.HasFlag` calls.
Roslyn proves the boxing conversion; focused disassembly/benchmarks must still determine whether the current
JIT eliminates it.

Known gaps are explicit rather than silently inferred: allocations in field/auto-property initializers are
omitted until their owner can be mapped to `.ctor`/`.cctor`; collection expressions, async state machines,
and allocation-returning library APIs are follow-ons. Attribute metadata is explicitly excluded because its
object/array-shaped arguments do not execute at the usage site.

### Lowering, strings, cardinality, and size result

The second slice adds structured evidence to every allocation fact and derived allocation effect:

- mechanism (`object_creation`, `array_creation`, `boxing`, `implicit_params`, `delegate`, `closure`,
  `iterator_state_machine`, `string_range`, `string_concat`, `string_interpolation`);
- cardinality (`per_evaluation`, `per_scope`, `cached_first_use`, `conditional`);
- nullable shallow bytes, confidence, and an assumption-bearing basis string.

High-confidence compiler lowering is source-attributed without a production IL dependency: expanded non-empty
`params` arrays, iterator creation at the caller, implicit delegates, capturing-lambda/local-function closure
scopes, runtime string concatenation/interpolation, and `string[Range]`. Existing arrays, omitted params,
constant-folded strings, full/empty string ranges, `FormattableString`, custom/non-string interpolation,
`AsSpan`, async state machines, and attribute metadata are negative controls. Explicit delegate construction
is retained as ordinary object creation. Nullable boxing is conditional and reports the underlying boxed type.

The owned `CoreAllocations` playground produces **18** allocation effects across all mechanisms. Its RCDATA
regression pair is deliberately direct: `rawEndTag[7..]` emits
`alloc:object System.String [string_range, conditional]`; `rawEndTag.AsSpan(7)` emits no allocation effect.
The expanded three-element `params int[]` reports an estimated 40 shallow bytes. Constant concatenation and
interpolation emit nothing.

AngleSharp.Core recalibration over the same 705-file target produced **1,307** facts, versus the original 861:

- 760 object creation, 16 array creation, and 86 boxing facts;
- 229 delegate and 86 closure facts;
- 53 iterator-state-machine call sites and 24 implicit params arrays;
- 35 runtime string concatenations and 18 runtime string interpolations;
- zero `string_range` facts in the fixed checkout (`_rawEndTag.AsSpan(7)` remains allocation-free);
- 878 sites with conservative x64 shallow-byte estimates;
- 68 loop-amplified effects after suppressing `looped_effect` on `cached_first_use` delegates
  (`cached-looped=0`).

The largest static estimates immediately identify the intentional 8,224-byte `char[]` and 4,120-byte
`byte[]` buffers in `WritableTextSource`. Parser calibration also surfaced compiler-cached lookup delegates
inside tokenizer loops without misclassifying them as per-iteration allocations.

## AngleSharp-core evaluation

Index the core project itself, not the whole solution, so benchmarks/build tooling do not dominate the result:

```pwsh
rig index C:\work\AngleSharp-core-tokenizer-consumer-build\src\AngleSharp\AngleSharp.Core.csproj
rig derive --only alloc --format tsv
```

Evaluate usefulness rather than raw count:

- inspect every reported allocation in representative tokenizer/parser/query paths;
- confirm struct construction is not misclassified as managed allocation;
- confirm arrays and boxing are represented distinctly;
- check whether loop/guard context identifies actionable repeated sites;
- compare the highest-value candidates with the existing AngleSharp BenchmarkDotNet allocation results and,
  where needed, gcdump or disassembly;
- record important measured allocation sites the detector misses; those misses determine the next semantic
  category.

The earlier rule-only experiment over `System.Linq.Enumerable` terminals found 18 sites across the full
solution, including six build-tool sites. It proved the existing effect projection works, but also proved that
LINQ-only detection is too narrow and solution-level calibration is noisy. Keep it as discarded calibration,
not the product design.

## Acceptance

- Focused fixtures cover class construction, struct construction as a negative control, explicit and implicit
  arrays, boxing, looped allocation, guarded allocation, and hoisted allocation.
- `derive --only alloc` emits stable human and TSV output using the existing effect surface without any
  allocation rule in the effective rule set.
- `tree`/`reaches` can surface the same effects without special allocation code.
- Adding or removing repository rules does not change the core allocation set.
- AngleSharp core is re-indexed and the result is reviewed against actual source and at least one measured
  parser/tokenizer workload.
- Limitations around shallow size, execution count, JIT elimination, retention, and dynamic array lengths are
  visible in documentation/output.

## Current recommendation

Build value-copy facts next using the same size/evidence vocabulary. Keep allocation-returning library method
summaries explicit, core-owned, and measurement-backed; do not infer allocation merely because an API returns
`string` or another reference type. Retain IL scanning as a development/calibration backstop for compiler
lowerings that cannot be attributed soundly from Roslyn source semantics, not as a required indexing stage.
