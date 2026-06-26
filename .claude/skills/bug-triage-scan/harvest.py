#!/usr/bin/env python3
"""Harvest bug-triage candidates from GitLab for the resumable backward scan.

Stateless on purpose: the AGENT owns the cursor (the frontier block in the scan
doc). This script just answers "given this frontier, what are the next N closed
bug issues older than it?", "what reverts/hotfixes exist (the escape-signal
index)?", and "show me the full detail of one item".

ONE candidate spine, ONE cursor (see SKILL.md):
  - list    : closed Severity::* bug issues, ordered by closed_at desc. THE cursor.
  - reverts : the revert/hotfix SIGNAL INDEX (a HINT, never a filter) — revert/
              hotfix-titled merged MRs PLUS orphan direct-to-main revert/hotfix
              commits. Each back-links to its culprit issue/branch. Whole-fetched
              (small set), no cursor. Two uses: annotate an issue candidate as
              `reverted` (confidence boost), and catch-net any revert whose culprit
              issue is NOT in the Severity spine.

Backward scan = issues with closed_at STRICTLY LESS THAN the frontier (older), not
in --seen, capped at --batch. First run passes the cutoff as the frontier.

Usage:
  harvest.py list     --project 9 --before-issue ISO --batch 25 [--seen seen.json]
  harvest.py reverts  --project 9 [--clone c:/git/meddbase-main-application] [--since 2023-01-01]
  harvest.py show     --project 9 --mr 10413
  harvest.py show     --project 9 --issue 4460

All times are ISO-8601 UTC (e.g. 2026-06-13T00:00:00Z). Output is JSON on stdout.
"""
import argparse
import json
import re
import subprocess
import sys

# Revert/hotfix title shapes for the SIGNAL INDEX (a hint, not a candidate gate).
# Each is a separate GitLab title search (OR'd by union).
REVERT_TITLE_TERMS = ["Revert", "broke", "regression", "hotfix"]
# The reliable "this was a real defect" signal — the candidate spine.
SEVERITY_LABELS = ["Severity::Critical", "Severity::Major", "Severity::Average", "Severity::Minor"]
DEFAULT_CLONE = "c:/git/meddbase-main-application"
# Parse the culprit issue out of a revert title, e.g. Revert "Merge branch '2892-foo' into 'main'".
_CULPRIT_BRANCH = re.compile(r"['\"](\d+)-")
_CULPRIT_CLOSES = re.compile(r"[Cc]loses?\s+#(\d+)")


def gl(path):
    """Run `glab api <path>` and return parsed JSON (or raise with stderr)."""
    r = subprocess.run(["glab", "api", path], capture_output=True, text=True, encoding="utf-8")
    if r.returncode != 0:
        raise RuntimeError(f"glab api {path} failed: {r.stderr.strip()}")
    return json.loads(r.stdout)


def gl_pages(path, max_pages=5, per_page=100):
    """Fetch up to max_pages of a list endpoint, concatenated. `path` must have no page param."""
    out = []
    sep = "&" if "?" in path else "?"
    for page in range(1, max_pages + 1):
        chunk = gl(f"{path}{sep}per_page={per_page}&page={page}")
        if not chunk:
            break
        out.extend(chunk)
        if len(chunk) < per_page:
            break
    return out


def older_than(value, frontier):
    """True if ISO time `value` is strictly older than `frontier` (None frontier => always)."""
    if not frontier or frontier.lower() == "none":
        return True
    return bool(value) and value < frontier


def harvest_issues(project, before, batch, seen):
    by_iid = {}
    for label in SEVERITY_LABELS:
        path = f"projects/{project}/issues?state=closed&labels={label}&order_by=updated_at&sort=desc"
        for i in gl_pages(path, max_pages=3):
            by_iid[i["iid"]] = i
    cands = [
        {
            "stream": "issue",
            "iid": i["iid"],
            "ref": f"#{i['iid']}",
            "title": i["title"],
            "closed_at": i.get("closed_at"),
            "labels": i.get("labels") or [],
            "web_url": i.get("web_url"),
        }
        for i in by_iid.values()
        if older_than(i.get("closed_at"), before) and f"#{i['iid']}" not in seen
    ]
    cands.sort(key=lambda c: c["closed_at"] or "", reverse=True)
    return cands[:batch]


def cmd_list(a):
    seen = set()
    if a.seen:
        with open(a.seen, encoding="utf-8") as f:
            seen = set(json.load(f))
    # ONE candidate spine, ONE cursor: closed Severity bug issues, newest→oldest.
    print(json.dumps({"issues": harvest_issues(a.project, a.before_issue, a.batch, seen)}, indent=2))


def culprit_issue(title, description=""):
    """Best-effort: pull the culprit issue # out of a revert/hotfix title or body."""
    m = _CULPRIT_BRANCH.search(title) or _CULPRIT_CLOSES.search(description or "")
    return f"#{m.group(1)}" if m else None


def revert_mrs(project):
    out = {}
    for term in REVERT_TITLE_TERMS:
        path = f"projects/{project}/merge_requests?state=merged&search={term}&in=title"
        for m in gl_pages(path, max_pages=3):
            out[m["iid"]] = {
                "via": "mr", "ref": f"!{m['iid']}", "date": m.get("merged_at"),
                "title": m["title"], "source_branch": m.get("source_branch"),
                "culprit_issue": culprit_issue(m["title"], m.get("description") or ""),
                "web_url": m.get("web_url"),
            }
    return list(out.values())


def orphan_revert_commits(project, clone, since):
    """Revert/hotfix-titled commits on main with NO associated MR (direct pushes)."""
    log = subprocess.run(
        ["git", "-C", clone, "log", "main", "--no-merges", f"--since={since}",
         "--pretty=%H%x09%cI%x09%s"],
        capture_output=True, text=True, encoding="utf-8",
    )
    if log.returncode != 0:
        sys.stderr.write(f"[warn] git log on {clone} failed; orphan-commit signal skipped: {log.stderr.strip()}\n")
        return []
    out = []
    for line in log.stdout.splitlines():
        if not line.strip():
            continue
        sha, date, title = line.split("\t", 2)
        if not any(k in title.lower() for k in ("revert", "hotfix")):
            continue
        mrs = gl(f"projects/{project}/repository/commits/{sha}/merge_requests")
        if mrs:  # came via an MR (squash) — the MR index already covers it
            continue
        out.append({
            "via": "orphan-commit", "ref": sha[:12], "sha": sha, "date": date,
            "title": title, "culprit_issue": culprit_issue(title),
        })
    return out


def cmd_reverts(a):
    index = revert_mrs(a.project) + orphan_revert_commits(a.project, a.clone, a.since)
    index.sort(key=lambda r: r.get("date") or "", reverse=True)
    # by_issue: lookup the agent uses to annotate a candidate / find catch-net items.
    by_issue = {}
    for r in index:
        if r["culprit_issue"]:
            by_issue.setdefault(r["culprit_issue"], []).append(r["ref"])
    print(json.dumps({"index": index, "by_issue": by_issue}, indent=2))


def notes(project, kind, iid):
    """Human (non-system) notes, oldest-first, trimmed."""
    raw = gl_pages(f"projects/{project}/{kind}/{iid}/notes?sort=asc&order_by=created_at", max_pages=3)
    return [
        {"author": n["author"]["username"], "created_at": n["created_at"], "body": n["body"]}
        for n in raw if not n.get("system")
    ]


def cmd_show(a):
    if a.mr:
        m = gl(f"projects/{a.project}/merge_requests/{a.mr}")
        closes = gl(f"projects/{a.project}/merge_requests/{a.mr}/closes_issues")
        changes = gl(f"projects/{a.project}/merge_requests/{a.mr}/diffs?per_page=100")
        print(json.dumps({
            "ref": f"!{a.mr}", "title": m["title"], "state": m["state"],
            "merged_at": m.get("merged_at"), "author": m["author"]["username"],
            "source_branch": m.get("source_branch"), "web_url": m.get("web_url"),
            "description": m.get("description"),
            "closes_issues": [f"#{c['iid']}: {c['title']}" for c in closes],
            "files_changed": [d.get("new_path") for d in changes],
            "diffs": [{"path": d.get("new_path"), "diff": d.get("diff")} for d in changes],
            "notes": notes(a.project, "merge_requests", a.mr),
        }, indent=2))
    elif a.issue:
        i = gl(f"projects/{a.project}/issues/{a.issue}")
        rel = gl(f"projects/{a.project}/issues/{a.issue}/related_merge_requests")
        print(json.dumps({
            "ref": f"#{a.issue}", "title": i["title"], "state": i["state"],
            "closed_at": i.get("closed_at"), "labels": i.get("labels") or [],
            "severity": [l for l in (i.get("labels") or []) if l.startswith("Severity::")],
            "web_url": i.get("web_url"), "description": i.get("description"),
            "related_mrs": [f"!{r['iid']}: {r['title']} [{r['state']}]" for r in rel],
            "notes": notes(a.project, "issues", a.issue),
        }, indent=2))
    else:
        sys.exit("show needs --mr or --issue")


def main():
    p = argparse.ArgumentParser(description=__doc__)
    sub = p.add_subparsers(dest="cmd", required=True)

    pl = sub.add_parser("list", help="next batch of candidate issues older than the frontier")
    pl.add_argument("--project", required=True)
    pl.add_argument("--before-issue", required=True, help="issue frontier (closed_at) ISO, or 'none'")
    pl.add_argument("--batch", type=int, default=25)
    pl.add_argument("--seen", help="path to JSON array of already-triaged refs (!iid / #iid)")
    pl.set_defaults(func=cmd_list)

    pr = sub.add_parser("reverts", help="the revert/hotfix signal index (hint, not a filter)")
    pr.add_argument("--project", required=True)
    pr.add_argument("--clone", default=DEFAULT_CLONE, help="local main-app clone for orphan-commit detection")
    pr.add_argument("--since", default="2023-01-01", help="oldest commit date to scan for orphan reverts")
    pr.set_defaults(func=cmd_reverts)

    ps = sub.add_parser("show", help="full detail of one MR or issue")
    ps.add_argument("--project", required=True)
    ps.add_argument("--mr")
    ps.add_argument("--issue")
    ps.set_defaults(func=cmd_show)

    a = p.parse_args()
    a.func(a)


if __name__ == "__main__":
    main()
