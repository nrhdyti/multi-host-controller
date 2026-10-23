#!/usr/bin/env bash
# End-to-end demo against the docker-compose stack: deploys MinIO to all online hosts and polls until done.
set -euo pipefail
BASE=${BASE:-http://localhost:8080}
ADMIN_PASSWORD=${ADMIN_PASSWORD:-admin-change-me}
MINIO_PASSWORD=${MINIO_PASSWORD:-ChangeMe12345}

TOKEN=$(curl -fsS -X POST "$BASE/api/auth/login" -H 'Content-Type: application/json' \
  -d "{\"username\":\"admin\",\"password\":\"$ADMIN_PASSWORD\"}" | python3 -c 'import sys,json;print(json.load(sys.stdin)["token"])')
AUTH=(-H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json')

HOSTS=$(curl -fsS "${AUTH[@]}" "$BASE/api/hosts")
echo "Hosts:"
echo "$HOSTS" | python3 -c "
import sys, json
for h in json.load(sys.stdin):
    print('  %-10s %-8s caps=%s' % (h['hostname'], h['status'], ','.join(h['capabilities'])))"
IDS=$(echo "$HOSTS" | python3 -c "
import sys, json
print(json.dumps([h['id'] for h in json.load(sys.stdin) if h['status'] == 'Online']))")

DEP=$(curl -fsS -X POST "${AUTH[@]}" "$BASE/api/deployments" -d "{
  \"applicationName\":\"minio\",\"hostIds\":$IDS,
  \"variables\":{\"minio_root_user\":\"minio-admin\",\"minio_root_password\":\"$MINIO_PASSWORD\"}}" \
  | python3 -c 'import sys,json;print(json.load(sys.stdin)["id"])')
echo "Deployment $DEP started"

for _ in $(seq 1 90); do
  STATUS=$(curl -fsS "${AUTH[@]}" "$BASE/api/deployments/$DEP" | python3 -c 'import sys,json;print(json.load(sys.stdin)["status"])')
  echo "  status: $STATUS"
  [ "$STATUS" != "InProgress" ] && break
  sleep 5
done
echo "Final: $STATUS"
echo "MinIO health (agent-1): $(curl -s -o /dev/null -w '%{http_code}' http://localhost:9100/minio/health/live)"
echo "MinIO console: http://localhost:9101  (user: minio-admin)"
