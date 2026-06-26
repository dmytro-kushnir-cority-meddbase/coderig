-- External-virtual-override SEAM discovery — RECEIVER-AWARE (validated on the MedDBase store, 2026-06-26).
--
-- Finds call sites where rig loses reach because the call binds to a method declared on an EXTERNAL base
-- while the EFFECT lives in a first-party override (the `redirectRules` candidate set — see
-- docs/backlog/todo/external-virtual-override-orphans.md item #1). Reproduces the two hand-authored LLBLGen
-- rules (EntityBase.Save / .Delete) as the top candidates, plus uncovered ones (e.g. the non-LLBLGen
-- DbDataContext.SubmitChanges → LINQ-to-SQL DataContext).
--
-- CRITICAL: this is RECEIVER-SCOPED, not target-level name matching. A naive "external target whose
-- same-NAME method is overridden first-party anywhere" both (a) floods with false positives — unrelated
-- types sharing a method name (PredicateExpression.Add ⨝ HL7Component.Add, List.Add, Option.Match …) — and
-- (b) WRONGLY EXCLUDES the real Save/Delete seams (some unrelated first-party type overrides the same
-- signature). The seam fires iff the call site's RECEIVER TYPE (or an ancestor up its base chain) overrides
-- the called simple name with a DIFFERENT signature, and NO override on that chain has the SAME signature
-- (same-sig => binds first-party => benign). The same-sig=0 (overload-mismatch) test is the true/benign
-- separator and also dissolves the ToString/GetHashCode/OnSave false hits without a separate effect filter.
--
-- Residual FN: ~4% of external invocations have a NULL ReceiverType (fluent/chained receivers) — invisible
-- to receiver-scoping; recover via the dispatch graph (`rig dispatch-fans --cause external-or-unbound`,
-- which already surfaces Save #1 / Delete #2 as actionable hubs). No NEW extraction needed for this family:
-- reference_facts(TargetInSource, ReceiverType) ⨝ dispatch_facts(Kind='override') ⨝
-- type_relation_facts(RelationKind='base') ⨝ symbol_facts (in-source test) suffice.

.timeout 180000

-- Overrides keyed by owner type + simple name + signature (first-party only: TargetMember ∈ symbol_facts).
CREATE TEMP TABLE ov AS
SELECT d.TargetMember AS impl_full,
  CASE WHEN instr(d.TargetMember,'(')>0 THEN substr(d.TargetMember, instr(d.TargetMember,'(')) ELSE '' END AS sig,
  substr(
    CASE WHEN instr(d.TargetMember,'(')>0 THEN substr(d.TargetMember,1,instr(d.TargetMember,'(')-1) ELSE d.TargetMember END,
    3,
    length(rtrim(
      CASE WHEN instr(d.TargetMember,'(')>0 THEN substr(d.TargetMember,1,instr(d.TargetMember,'(')-1) ELSE d.TargetMember END,
      replace(CASE WHEN instr(d.TargetMember,'(')>0 THEN substr(d.TargetMember,1,instr(d.TargetMember,'(')-1) ELSE d.TargetMember END,'.','')))-3
  ) AS owner_type,
  substr(
    CASE WHEN instr(d.TargetMember,'(')>0 THEN substr(d.TargetMember,1,instr(d.TargetMember,'(')-1) ELSE d.TargetMember END,
    length(rtrim(
      CASE WHEN instr(d.TargetMember,'(')>0 THEN substr(d.TargetMember,1,instr(d.TargetMember,'(')-1) ELSE d.TargetMember END,
      replace(CASE WHEN instr(d.TargetMember,'(')>0 THEN substr(d.TargetMember,1,instr(d.TargetMember,'(')-1) ELSE d.TargetMember END,'.','')))+1
  ) AS simple
FROM dispatch_facts d
WHERE d.Kind='override' AND EXISTS(SELECT 1 FROM symbol_facts s WHERE s.SymbolId=d.TargetMember);
CREATE INDEX ix_ov ON ov(owner_type, simple);

-- Direct base edges (derived -> base), stripping the 'T:' id prefix to match reference_facts.ReceiverType.
CREATE TEMP TABLE base1 AS
SELECT substr(TypeSymbolId,3) AS derived, substr(RelatedSymbolId,3) AS base
FROM type_relation_facts WHERE RelationKind='base';
CREATE INDEX ix_b1 ON base1(derived);

.mode column
.headers on
.width 50 8 9 9 38

WITH RECURSIVE anc(derived, anc_type) AS (
  SELECT derived, derived FROM base1          -- self
  UNION
  SELECT a.derived, b.base FROM anc a JOIN base1 b ON b.derived = a.anc_type   -- transitive base chain
),
ext AS (
  SELECT TargetSymbolId AS tgt, ReceiverType AS recv,
    CASE WHEN instr(TargetSymbolId,'(')>0 THEN substr(TargetSymbolId, instr(TargetSymbolId,'(')) ELSE '' END AS called_sig,
    substr(
      CASE WHEN instr(TargetSymbolId,'(')>0 THEN substr(TargetSymbolId,1,instr(TargetSymbolId,'(')-1) ELSE TargetSymbolId END,
      length(rtrim(
        CASE WHEN instr(TargetSymbolId,'(')>0 THEN substr(TargetSymbolId,1,instr(TargetSymbolId,'(')-1) ELSE TargetSymbolId END,
        replace(CASE WHEN instr(TargetSymbolId,'(')>0 THEN substr(TargetSymbolId,1,instr(TargetSymbolId,'(')-1) ELSE TargetSymbolId END,'.','')))+1
    ) AS simple
  FROM reference_facts
  WHERE RefKind='invocation' AND TargetInSource=0 AND ReceiverType IS NOT NULL AND ReceiverType<>''
),
hit AS (
  SELECT e.tgt, e.recv, e.called_sig, o.sig AS impl_sig, o.impl_full
  FROM ext e
  JOIN anc a ON a.derived = e.recv             -- receiver OR an ancestor...
  JOIN ov o ON o.owner_type = a.anc_type AND o.simple = e.simple   -- ...declares the override
)
SELECT tgt AS called_external_target,
  COUNT(*) AS join_rows,
  COUNT(DISTINCT recv) AS recv_types,
  SUM(CASE WHEN impl_sig = called_sig THEN 1 ELSE 0 END) AS same_sig_hits,   -- >0 => benign (binds first-party)
  MIN(CASE WHEN impl_sig <> called_sig THEN impl_full END) AS example_diff_override
FROM hit
GROUP BY tgt
HAVING same_sig_hits = 0           -- overload-mismatch only = true seams
ORDER BY recv_types DESC
LIMIT 25;
