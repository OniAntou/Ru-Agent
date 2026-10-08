#!/bin/sh
set -eu
candidate_root="${BENCHMARK_CANDIDATE_ROOT:-/app}"
test_root="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
mkdir -p /logs/verifier
printf '0\n' > /logs/verifier/reward.txt
dotnet build "$test_root/Grader.csproj" --nologo -p:BenchmarkCandidateRoot="$candidate_root" -o /tmp/cache-grader >&2
set +e
dotnet /tmp/cache-grader/Grader.dll > /logs/verifier/groups.json
status=$?
set -e
if [ "$status" -eq 0 ]; then reward=1; else reward=0; fi
printf '%s\n' "$reward" > /logs/verifier/reward.txt
exit "$status"
