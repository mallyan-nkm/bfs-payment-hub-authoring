#!/usr/bin/env bash
# Summarise `go test -v` output of the persistence suites: pass, fail and skip counts per suite.
# usage: go test ./tests/ -run TestSQLServer -v | tee run.log; scripts/suite-summary.sh run.log
set -euo pipefail
log="${1:?usage: suite-summary.sh <go test -v log>}"
awk '
  /^ *--- (PASS|FAIL|SKIP): TestSQLServer\/[^\/ ]+\/[^ ]+ / {
    split($3, p, "/"); suite = p[2]; verdict = $2; sub(":", "", verdict)
    n[suite, verdict]++; seen[suite] = 1; total[verdict]++
  }
  END {
    printf "%-60s %5s %5s %5s\n", "suite", "pass", "fail", "skip"
    for (s in seen) printf "%-60s %5d %5d %5d\n", s, n[s, "PASS"], n[s, "FAIL"], n[s, "SKIP"] | "sort"
    close("sort")
    printf "%-60s %5d %5d %5d\n", "total (test cases)", total["PASS"], total["FAIL"], total["SKIP"]
  }' "$log"
