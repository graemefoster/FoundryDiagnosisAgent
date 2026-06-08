---
name: foundry-agent-communications
description: Diagnose connectivity and invocation issues when calling another Azure AI Foundry Agent from the current agent or project. Covers same-project, cross-project, and cross-Foundry agent-to-agent communication via the Responses API. Trigger phrases include "call another agent", "agent reference not working", "cross-foundry agent", "agent-to-agent communication", "responses API agent_reference", "can't reach remote agent", "foundry agent chaining", "multi-agent", "agent handoff failed".
---

# Foundry Agent-to-Agent Communication Diagnostics

> **⚠️ IDE / Claude Code Limitation:** When running outside the Foundry hosted agent (e.g. in VS Code, Claude Code, or other IDE environments), Steps 4 and 5 (TCP connectivity tests, `dig`, `nc`, and direct HTTPS probes to the target endpoint) cannot diagnose low-level Foundry network issues. These steps rely on being on the same network as the Foundry agent. In IDE mode, focus on Steps 1–3 (topology, agent existence, and token/permission validation) which use ARM and data-plane API calls that work from any environment with `az` CLI access. If connectivity is suspected, recommend the user run the full agent-hosted diagnostics.

Use this skill when a user needs to diagnose issues calling another Foundry Agent — whether in the same project, a different project on the same Foundry, or a completely separate Foundry instance.

## How Agent-to-Agent Communication Works

Foundry agents invoke other agents via the **Responses API** using an `agent_reference` in the request body. The calling agent (or orchestrator) sends a POST to:

```
POST {target_endpoint}/openai/v1/responses
```

With payload:

```json
{
  "input": "<message to send to the target agent>",
  "agent_reference": {
    "name": "<target_agent_name>",
    "type": "agent_reference"
  }
}
```

For multi-turn conversations with the target agent, include `previous_response_id`:

```json
{
  "input": "<follow-up message>",
  "previous_response_id": "<response_id_from_previous_call>",
  "agent_reference": {
    "name": "<target_agent_name>",
    "type": "agent_reference"
  }
}
```

### Endpoint Patterns

| Scenario | Target Endpoint |
|---|---|
| Same project | `https://{resource_name}.services.ai.azure.com/api/projects/{project_name}` |
| Different project, same Foundry | `https://{resource_name}.services.ai.azure.com/api/projects/{other_project_name}` |
| Different Foundry entirely | `https://{other_resource_name}.services.ai.azure.com/api/projects/{project_name}` |

### Authentication

The caller must present a Bearer token with audience `https://ai.azure.com/`:

```
Authorization: Bearer <token>
```

Token acquisition:

- **Hosted (MSI)**: `GET {IDENTITY_ENDPOINT}?resource=https://ai.azure.com/&api-version=2019-08-01` with header `X-IDENTITY-HEADER: {IDENTITY_HEADER}`
- **Local dev**: `az account get-access-token --resource https://ai.azure.com/ --query accessToken -o tsv`

## Common Failure Modes

| Symptom | Likely Cause |
|---|---|
| 401 Unauthorized | Token audience wrong, token expired, or identity not assigned |
| 403 Forbidden | Caller identity lacks RBAC on target project (needs `Azure AI Developer` or similar) |
| 404 Not Found | Agent name wrong, agent not deployed, or endpoint URL malformed |
| 409 Conflict | Agent version mismatch or concurrent access issue |
| Network timeout | VNet/NSG blocking outbound to target Foundry endpoint, or DNS resolution failure |
| DNS resolution failure | Cross-Foundry endpoint not resolvable from the calling environment (private DNS zone mismatch) |
| TLS handshake failure | Proxy or firewall intercepting traffic to `*.services.ai.azure.com` |

## Diagnostic Workflow

### Step 1: Identify the Communication Topology

Determine:
1. **Source**: Which agent/project is making the call? (Use `FOUNDRY_PROJECT_ENDPOINT` or `AZURE_AI_PROJECT_ENDPOINT`)
2. **Target**: What is the target endpoint and agent name?
3. **Same or cross-Foundry**: Compare the `{resource_name}` portion of source and target endpoints.

### Step 2: Validate Target Agent Exists

Call the Agents API on the target project to confirm the agent is deployed:

```bash
curl -s -X GET "${TARGET_ENDPOINT}/agents/${TARGET_AGENT_NAME}?api-version=v1" \
  -H "Authorization: Bearer ${TOKEN}" \
  -H "Content-Type: application/json"
```

Expected: 200 with agent object. If 404, the agent name is wrong or not deployed.

### Step 3: Validate Token and Permissions

1. Acquire a token for `https://ai.azure.com/` using the caller's identity.
2. Attempt to call the target endpoint with that token.
3. If 401: check token audience claim (`aud` should be `https://ai.azure.com`). Decode with `echo $TOKEN | cut -d'.' -f2 | base64 -d 2>/dev/null | jq .aud`
4. If 403: the caller's managed identity needs RBAC on the target project. Check:
   - `Azure AI Developer` role on the target project or its parent resource group
   - For cross-Foundry: the caller's identity must be granted access in the **target** Foundry's tenant/project

### Step 4: Test Connectivity to Target Endpoint

```bash
# DNS resolution
dig +short $(echo "${TARGET_ENDPOINT}" | sed 's|https://||' | cut -d'/' -f1)

# TCP connectivity (port 443)
nc -zv $(echo "${TARGET_ENDPOINT}" | sed 's|https://||' | cut -d'/' -f1) 443

# Full HTTPS test
curl -sv -o /dev/null "${TARGET_ENDPOINT}/openai/v1/responses" \
  -H "Authorization: Bearer ${TOKEN}" \
  -H "Content-Type: application/json" \
  -d '{"input":"test","agent_reference":{"name":"'"${TARGET_AGENT_NAME}"'","type":"agent_reference"}}' 2>&1 | head -30
```

### Step 5: Attempt the Actual Agent Call

```bash
curl -s -X POST "${TARGET_ENDPOINT}/openai/v1/responses" \
  -H "Authorization: Bearer ${TOKEN}" \
  -H "Content-Type: application/json" \
  -d '{
    "input": "Hello, are you available?",
    "agent_reference": {
      "name": "'"${TARGET_AGENT_NAME}"'",
      "type": "agent_reference"
    }
  }'
```

Check the response for:
- Success (200): Confirm `id` field is present — communication works.
- Error: Parse the error object for `code` and `message`.

### Step 6: Cross-Foundry Specific Checks

When calling an agent on a **different** Foundry instance:

1. **Network path**: Does the calling environment have outbound access to the target Foundry FQDN? (Check NSG, firewall, VNet rules)
2. **DNS**: Is the target FQDN resolvable? (Private Link may prevent public resolution)
3. **Identity trust**: The calling identity must be recognizable by the target tenant. For cross-tenant scenarios, verify:
   - Multi-tenant app registration, or
   - The caller identity is a guest in the target tenant, or
   - Federated credentials are configured
4. **RBAC**: Even with network access, the caller needs data-plane RBAC on the target project.

## Required Inputs

Read from environment — do not ask the user unless unavailable. They may provide alternate target endpoint or agent name if diagnosing a specific call to a different Foundry.

| Variable | Purpose |
|---|---|
| `FOUNDRY_PROJECT_ENDPOINT` / `AZURE_AI_PROJECT_ENDPOINT` | Source (calling) endpoint |
| `IDENTITY_ENDPOINT` + `IDENTITY_HEADER` | Token acquisition (hosted) |
| `FOUNDRY_AGENT_NAME` | Name of this (calling) agent |

Ask the user for:
- Target agent name
- Target endpoint (if different from source)

## Output Format

1. **Communication topology** — source → target summary
2. **Target agent validation** — exists / not found / access denied
3. **End-to-end test result** — actual Responses API call outcome
4. **Root cause** — most likely failure reason with confidence
5. **Remediation** — specific steps to fix, least disruptive first
6. **Verification** — command to confirm the fix works

## Guardrails

- Never send real user data as the `input` in test calls — use a neutral probe like "Hello, are you available?"
- Do not modify RBAC or network rules automatically — recommend changes only.
- Clearly distinguish between same-project, cross-project, and cross-Foundry scenarios as permissions differ.
- If token decode reveals a cross-tenant situation, flag it immediately as a likely root cause.
