#!/bin/sh
set -eu
mkdir -p /logs/verifier
test_root="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
if python3 "$test_root/grade.py" > /tmp/grade-output.json; then status=0; else status=$?; fi
cat /tmp/grade-output.json
cp /tmp/grade-output.json /logs/verifier/groups.json
if [ "$status" -eq 0 ]; then printf '1' > /logs/verifier/reward.txt; else printf '0' > /logs/verifier/reward.txt; fi
exit "$status"
