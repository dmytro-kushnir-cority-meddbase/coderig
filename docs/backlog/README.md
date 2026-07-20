# rig — feature backlog

Forward-looking feature specs not yet scheduled. Distinct from
[rig-review-issues.md](../archive/rig-review-issues.md) (the MR-!10645 audit punch-list). Promote an item to a branch
+ commits when picked up; convert to a GitHub issue (`gh issue create`, remote `dv00d00/coderig`) if tracked
externally.

---

## Convention

```
docs/backlog/
  todo/       proposed / not-started, plus remaining work blocked on unavailable external corpora
  progress/   has shipped AND locally actionable open sub-items, or actively in-flight
  done/       fully shipped / superseded / retracted / parked-wontfix / reference
```

One file per issue. **The index is `ls docs/backlog/*/`** — no maintained index file.

`done/` also holds reference logs, parked/wontfix items, and session notes with recorded findings.
