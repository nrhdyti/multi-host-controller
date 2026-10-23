# API reference

Interactive docs: `http://localhost:8080/swagger` (enabled in the compose stack). Admin calls need `Authorization: Bearer <jwt>` from `POST /api/auth/login`.
Enums are returned as strings; JSON is camelCase.

## Auth
| Method | Path | Notes |
|---|---|---|
| POST | `/api/auth/login` | `{username, password}` -> `{token, expiresAt}`. Rate limited. |

## Enrollment (Admin)
| Method | Path | Notes |
|---|---|---|
| POST | `/api/enrollment-tokens` | `{note?, validMinutes}` -> `{token, expiresAt}`. Token shown once, single use. |

## Hosts (Admin)
| Method | Path | Notes |
|---|---|---|
| GET | `/api/hosts` | All hosts with status and latest CPU/memory/disk. |
| GET | `/api/hosts/{id}` | One host. |
| GET | `/api/hosts/{id}/metrics?minutes=60` | Heartbeat history (max 1440 minutes). |
| DELETE | `/api/hosts/{id}` | Deregister: revokes the agent token, fails queued jobs, keeps history. |

## Applications (Admin)
| Method | Path | Notes |
|---|---|---|
| GET | `/api/applications` | Packages incl. variable schema and prerequisites. |
| GET | `/api/applications/{name}` | One package. |
| POST | `/api/applications` | Create/update a package: playbooks (YAML text), variables, `requiredCapabilities`, `dependencies`. |
| POST | `/api/applications/reload` | Re-read `playbooks/` from disk. |

## Deployments (Admin)
| Method | Path | Notes |
|---|---|---|
| POST | `/api/deployments` | `{applicationName, hostIds[], variables{}}`. 400 invalid variables, 404 unknown app, 422 offline host / missing prerequisite / unmet dependency. |
| GET | `/api/deployments?take=50` | Newest first, with per-host jobs. |
| GET | `/api/deployments/{id}` | One deployment. Status: `InProgress`, `Succeeded`, `Failed`. |
| GET | `/api/deployments/jobs/{jobId}` | Job detail with full log; secret variables masked. |
| POST | `/api/deployments/{id}/rollback` | Runs the rollback playbook where the deployment succeeded. |

## Agent API (role `Agent`, except register)
| Method | Path | Notes |
|---|---|---|
| POST | `/api/agent/register` | Anonymous; needs a valid enrollment token. Re-registering a hostname re-issues credentials. |
| POST | `/api/agent/heartbeat` | CPU %, memory MB, disk GB, capabilities. 401 if the host was deregistered. |
| GET | `/api/agent/jobs/next` | 200 + job, or 204. One active job per host. |
| POST | `/api/agent/jobs/{id}/log` | Append log text. |
| POST | `/api/agent/jobs/{id}/result` | `{status: Succeeded|Failed|RolledBack|RollbackFailed, message, verified}`. |
| DELETE | `/api/agent/self` | Agent-initiated deregistration. |

## SignalR
`/hubs/status` (Admin JWT via `access_token` query). Events: `hostsChanged`, `deploymentChanged(id)`.

## Example
```bash
TOKEN=$(curl -s localhost:8080/api/auth/login -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"admin-change-me"}' | jq -r .token)
curl -s localhost:8080/api/hosts -H "Authorization: Bearer $TOKEN" | jq
curl -s localhost:8080/api/deployments -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"applicationName":"minio","hostIds":["<host-id>"],"variables":{"minio_root_password":"ChangeMe12345"}}'
```
