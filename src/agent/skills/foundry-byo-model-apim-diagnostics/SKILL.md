---
name: foundry-byo-model-apim-diagnostics
description: Diagnose BYO (Bring Your Own) Model issues when Azure AI Foundry Agents use an Azure API Management connection. Validates the full chain from agent model property, through the ARM connection resource, to the actual APIM endpoint. Identifies misconfigurations in connection setup, credential issues, network reachability, and model name mismatches. Trigger phrases include "byo model not working", "apim model connection", "agent can't reach model", "bring your own model", "custom model connection", "apim connection failing", "model misconfiguration", "connection model mismatch".
---

# Foundry BYO Model (APIM) Diagnostics

Use this skill when a user's Azure AI Foundry Agent fails to reach a model served through Azure API Management (APIM) via a BYO Model connection.

## Goal

Systematically diagnose the connection chain from a Foundry Agent → ARM Connection resource → APIM endpoint, replicate the exact call the agent would make, and identify the failure point.

## Common Failure Modes

1. **Model name mismatch** — the model name in the agent definition doesn't match a model listed in the connection's metadata
2. **Target URL misconfiguration** — the connection target URL doesn't correctly point to the APIM gateway
3. **Credential issues** — API key is invalid, expired, or the auth header format is wrong
4. **Network unreachable** — APIM endpoint is not reachable from the Foundry agent (DNS, firewall, private endpoint)
5. **Deployment path mismatch** — `deploymentInPath` metadata doesn't match how APIM routes requests
6. **APIM backend misconfiguration** — APIM receives the request but can't route to the actual model backend

## Required Inputs

Ask the user for:

1. **Agent name** — the name of the Foundry Agent that is failing (this is the agent ID on the Foundry data plane)
2. **Foundry project endpoint** — e.g. `https://{account}.services.ai.azure.com/api/projects/{project}` (or read from `FOUNDRY_PROJECT_ENDPOINT` / `AZURE_AI_PROJECT_ENDPOINT` environment variables)
3. **ARM scope** — subscription ID, resource group, and Cognitive Services account name (or read from `FOUNDRY_PROJECT_ARM_ID` environment variable)

## Authentication

**Check `IDENTITY_ENDPOINT` first.** If set, use MSI. If NOT set (local development), use Azure CLI.

### Hosted (MSI — `IDENTITY_ENDPOINT` is set):

ARM Token:
```
GET {IDENTITY_ENDPOINT}?resource=https://management.azure.com/&api-version=2019-08-01
X-IDENTITY-HEADER: {IDENTITY_HEADER}
```

Foundry Data Plane Token:
```
GET {IDENTITY_ENDPOINT}?resource=https://ai.azure.com/&api-version=2019-08-01
X-IDENTITY-HEADER: {IDENTITY_HEADER}
```

### Local development (no `IDENTITY_ENDPOINT`):

ARM Token:
```bash
az account get-access-token --resource https://management.azure.com/ --query accessToken -o tsv
```

Foundry Data Plane Token:
```bash
az account get-access-token --resource https://ai.azure.com/ --query accessToken -o tsv
```

## Diagnostic Workflow

### Step 1: Fetch the Agent Definition

Call the Foundry data plane to get the agent:

```
GET https://{foundry}.services.ai.azure.com/api/projects/{project}/agents/{agentName}?api-version=2025-11-15-preview
Authorization: Bearer {foundry_token}
```

Extract the `model` property. It is formatted as `{connectionName}/{modelName}`.

Parse:
- `connectionName` — the part before the first `/`
- `modelName` — the part after the first `/`

Report these values to the user.

### Step 2: Fetch the ARM Connection Resource

Using the connection name from Step 1, fetch the connection:

```
GET https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.CognitiveServices/accounts/{accountName}/connections/{connectionName}?api-version=2026-03-15-preview
Authorization: Bearer {arm_token}
```

Verify:
- The connection exists (not 404)
- `properties.category` is `"ApiManagement"` (confirms APIM type)
- `properties.authType` is recognized (`"ApiKey"` or `"AAD"`)
- `properties.target` contains a valid URL

Extract and report:
- `properties.target` — the APIM endpoint URL
- `properties.metadata.deploymentInPath` — **REQUIRED** whether model/deployment goes in URL path (`"true"` or `"false"`)
- `properties.metadata.inferenceAPIVersion` — OPTIONAL api-version for chat completions calls
- `properties.metadata.deploymentAPIVersion` — OPTIONAL api-version for discovery calls
- `properties.metadata.authConfig` — OPTIONAL custom auth header configuration (JSON string)
- `properties.metadata.customHeaders` — OPTIONAL additional headers for requests
- `properties.metadata.models` — OPTIONAL JSON array of available models (static discovery)
- `properties.metadata.modelDiscovery` — OPTIONAL dynamic discovery endpoint configuration

### Step 3: Validate the Model Name and Resolve the API Deployment Name

#### Model Identifier Mapping

There are three distinct names involved in BYO Model connections:

1. **Agent model reference**: `connectionName/modelName` — what the agent uses (e.g., `my-apim-connection/gpt-4o-deployment`)
2. **Connection deployment name**: `metadata.models[].name` — must match `modelName` from the agent. This is the deployment name used in APIM API calls (e.g., `gpt-4o-deployment`)
3. **Provider model name**: `metadata.models[].properties.model.name` — the actual model name from the provider (e.g., `gpt-4o`). This is NOT sent to APIM — it's used for Foundry UI/model catalog display only.

Example mapping:
```
Agent model property:        my-apim-connection/gpt-4o-deployment
Connection deployment name:  gpt-4o-deployment  ← must match agent's modelName, used in APIM URL/body
Provider model name:         gpt-4o             ← for Foundry UI only, not sent to APIM
```

**Key point**: The deployment name sent to APIM in the request body or URL path is `models[].name` (e.g., `gpt-4o-deployment`), NOT `models[].properties.model.name`. The latter is purely the provider's model identifier for display purposes.

#### Validation

Parse the `properties.metadata.models` JSON array. Each entry has:
```json
{
  "name": "gpt-4o-deployment",
  "properties": {
    "model": {
      "name": "gpt-4o",
      "version": "2024-11-20",
      "format": "OpenAI"
    }
  }
}
```

Check that `modelName` from Step 1 matches one of the `name` fields in the models array.

If no match:
- **This is likely the problem.** Report the mismatch clearly.
- Show what model names ARE available on the connection.
- Suggest the user update the agent's model property to use one of the valid model names.

If match found, the **connection deployment name** (`models[].name`) is the value that will be used in the actual chat completions request body or URL path. The `models[].properties.model.name` is only the provider's model name and should be ignored for API call construction.

#### Dynamic Discovery (No `models` property)

If the connection metadata does **NOT** have a `models` array, the agent uses **dynamic discovery** — it calls the APIM endpoint at runtime to discover available models. This is a common source of failures.

**How dynamic discovery works:**

1. The agent reads `metadata.modelDiscovery` (if present) to get custom endpoints
2. If `modelDiscovery` is absent, APIM defaults are used:
   - List deployments: `GET {target}/deployments`
   - Get deployment: `GET {target}/deployments/{deploymentName}`
   - Provider format: `AzureOpenAI`
3. If `modelDiscovery` is present, it overrides the defaults:
   ```json
   {
     "modelDiscovery": {
       "listModelsEndpoint": "/custom/models",
       "getModelEndpoint": "/custom/models/{deploymentName}",
       "deploymentProvider": "OpenAI"
     }
   }
   ```
4. The `deploymentAPIVersion` metadata field (if set) is appended as `?api-version=` to discovery calls

**Diagnosing dynamic discovery failures:**

##### Step 3a: Determine the discovery endpoints

Read `metadata.modelDiscovery` from the connection. If absent, use defaults:
- `listModelsEndpoint` = `/deployments`
- `getModelEndpoint` = `/deployments/{deploymentName}`
- `deploymentProvider` = `AzureOpenAI`

##### Step 3b: Call the list deployments endpoint

```
GET {target}{listModelsEndpoint}
{authHeader}: {authValue}
```

Where `{authHeader}` and `{authValue}` are determined by `metadata.authConfig` (or default `api-key: {key}` if not set).

If `deploymentAPIVersion` is set, append `?api-version={deploymentAPIVersion}`.

Check:
- Does this endpoint return HTTP 200?
- If 404: the APIM does not expose this endpoint — either configure it in APIM or switch to static discovery (`models` array)
- If 401/403: credential or policy issue for the discovery endpoint specifically

##### Step 3c: Validate the response format

The response format depends on `deploymentProvider`:

**AzureOpenAI format** (default):
```json
{
  "value": [
    {
      "name": "gpt-4o-deployment",
      "properties": {
        "model": {
          "format": "OpenAI",
          "name": "gpt-4o",
          "version": "2024-11-20"
        }
      }
    }
  ]
}
```
The agent looks for deployments in the `value` array. Each entry's `name` is the deployment name.

**OpenAI format** (`deploymentProvider: "OpenAI"`):
```json
{
  "data": [
    {
      "id": "gpt-4o",
      "object": "model",
      "created": 1687882411,
      "owned_by": "openai"
    }
  ]
}
```
The agent looks in the `data` array. Each entry's `id` is both the deployment name and model name.

Validate:
- Is the response valid JSON?
- Does it use the correct top-level key (`value` for AzureOpenAI, `data` for OpenAI)?
- If the APIM returns a different format, report the mismatch and suggest setting the correct `deploymentProvider`

##### Step 3d: Check if the agent's model exists in discovered deployments

Parse the list response and check if `modelName` (from Step 1) appears as:
- A `name` field in `value[]` (AzureOpenAI format), OR
- An `id` field in `data[]` (OpenAI format)

If NOT found:
- **This is likely the problem.** The model the agent references does not exist on the APIM.
- List all discovered deployment names so the user can see what IS available.
- Suggest either:
  1. Adding the deployment to APIM
  2. Changing the agent's model property to reference an existing deployment
  3. Switching to static discovery with a `models` array in the connection metadata

##### Step 3e: Call the get-deployment endpoint

```
GET {target}{getModelEndpoint}   (replace {deploymentName} with the modelName)
{authHeader}: {authValue}
```

If `deploymentAPIVersion` is set, append `?api-version={deploymentAPIVersion}`.

Check:
- HTTP 200 confirms the specific deployment is accessible
- HTTP 404 means the list endpoint works but the specific deployment doesn't resolve (possible routing issue in APIM)
- Mismatched response format suggests wrong `deploymentProvider` setting

##### Common dynamic discovery failure modes

| Symptom | Likely Cause | Fix |
|---------|-------------|-----|
| 404 on list endpoint | APIM doesn't expose `/deployments` | Add the endpoint to APIM, or use `modelDiscovery` to point to the correct path, or use static `models` |
| 200 but empty `value`/`data` array | APIM returns no models | Check APIM backend configuration — ensure it returns the deployment list |
| 200 but model not in list | Deployment doesn't exist on APIM | Add the deployment or use a different model name |
| Wrong response format | `deploymentProvider` mismatch | Set `deploymentProvider` to match what APIM actually returns (`OpenAI` vs `AzureOpenAI`) |
| 401/403 on discovery | Auth not applied to discovery endpoint | Check APIM policies apply to the discovery paths too |
| Timeout | APIM backend unreachable for discovery | Check APIM backend health, timeout settings |

### Step 4: Fetch Connection Secrets

#### If `authType` is `"ApiKey"` and `useWorkspaceManagedIdentity` is `false`:

Call listSecrets to get the credentials: This will require a higher permission to Foundry like Contributor. But you can ask the user for the key.

```
POST https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.CognitiveServices/accounts/{accountName}/connections/{connectionName}/listSecrets?api-version=2026-03-15-preview
Authorization: Bearer {arm_token}
```

Extract `properties.credentials.key`.

**Never display the full key to the user.** Show only the first 4 characters followed by `***`.

#### If `authType` is `"AAD"`:

- The connection has empty credentials (`"credentials": {}`)
- The agent authenticates to APIM using the Foundry project's managed identity
- Validate:
  - APIM has a `validate-azure-ad-token` policy or equivalent configured
  - The Foundry project managed identity (client ID) is in the allowed audience/app IDs
  - The correct token audience is configured (typically the APIM app registration)
- For testing, acquire a token for the correct audience using MSI and use `Authorization: Bearer {token}` instead of an API key
- The `authConfig` metadata field is NOT used with AAD — the agent sends a Bearer token directly

### Step 5: Replication Test

**IMPORTANT**: The test you perform depends on whether the connection uses static models or dynamic discovery.

#### If Dynamic Discovery is in play (no `models` array in metadata):

**Do NOT test chat/completions.** The agent's first call is to the discovery endpoint. Test that instead.

The discovery endpoints were already identified in Step 3a. Perform the tests from Steps 3b–3e:

1. Call the **list deployments** endpoint (Step 3b)
2. Validate the **response format** matches the expected `deploymentProvider` (Step 3c)
3. Check if the **agent's model exists** in the discovered list (Step 3d)
4. Call the **get-deployment** endpoint for the specific model (Step 3e)

Only if ALL discovery steps succeed AND you want to validate end-to-end connectivity to the model itself, proceed to construct a chat/completions URL using the information from the discovery response.

#### If Static Models are defined (`models` array exists in metadata):

The agent already knows which models are available. Test the actual inference endpoint.

#### URL Construction for Chat/Completions (static models only, or after successful discovery)

Build the URL the agent would call:

1. Start with `properties.target` (e.g., `https://your-apim-gateway.azure-api.net/myapi`)
2. Normalize: trim any trailing `/`
3. Apply `deploymentInPath` rules below
4. Append `?api-version={inferenceAPIVersion}` if that metadata field is set

#### URL Construction Based on `deploymentInPath`

Use the **connection deployment name** (from Step 3, e.g., `gpt-4o-deployment`), NOT the provider model name.

**If `deploymentInPath` is `"true"`:**
```
POST {target}/deployments/{connectionDeploymentName}/chat/completions
```

**If `deploymentInPath` is `"false"`:**
```
POST {target}/chat/completions
```
Include `"model": "{connectionDeploymentName}"` in the request body.

**`deploymentInPath` is REQUIRED** — if it's missing, the connection is misconfigured.

#### API Version

Use `metadata.inferenceAPIVersion` if present — this is the spec-defined field for chat completions API version:
```
?api-version={inferenceAPIVersion}
```

- If `inferenceAPIVersion` is set, always append it as a query parameter
- If `inferenceAPIVersion` is NOT set, the agent uses a default API version
- Do NOT use `deploymentAPIVersion` here — that is only for discovery calls

#### Auth Header Construction

Auth headers are determined by the `metadata.authConfig` field (a JSON string that must be parsed):

```json
{
  "type": "api_key",
  "name": "x-api-key",
  "format": "Key {api_key}"
}
```

- `name` = the header name to use (e.g., `x-api-key`, `Authorization`)
- `format` = template for the header value; replace `{api_key}` with the actual credential

**If `authConfig` is NOT specified**, use the default APIM convention:
```
api-key: {raw_subscription_key}
```

Common patterns:
- Default: `api-key: {key}`
- Bearer: `Authorization: Bearer {key}`
- Custom: `X-API-Token: Token {key}`

Also parse and include any `metadata.customHeaders` (JSON object of additional headers to add to all requests).

#### Test Request (only for chat/completions)

Send a minimal chat completions request:
```json
{
  "messages": [{"role": "user", "content": "Say hello"}],
  "model": "{connectionDeploymentName}",
  "max_tokens": 5
}
```

Report: HTTP status, response time, response body (summarized).

### Step 6: Interpret Results

| HTTP Status | Likely Cause | Recommendation |
|---|---|---|
| 200 | Connection works — issue may be transient or in agent config | Verify agent model property matches exactly |
| 400 | Malformed request — wrong api-version, unsupported body field, or model name not recognized by backend | Check api-version, request body format, model name in body |
| 401/403 | Credential invalid, APIM subscription key rejected, or managed identity not authorized | Check key validity, APIM subscription status, validate-azure-ad-token policy |
| 404 | Endpoint path wrong — deployment name or URL structure mismatch | Check `deploymentInPath`, target URL, model/deployment name routing in APIM |
| 429 | Rate limited by APIM or backend | Check APIM rate limit policies, backend quota |
| 500 | APIM internal error or policy execution failure | Check APIM policy configuration, inbound/outbound/on-error policies |
| 502/503/504 | APIM backend failure — APIM can't reach its own backend | Check APIM backend configuration, health probes, backend timeout settings |
| Connection timeout | Network path blocked | Check DNS resolution, firewall rules, private endpoints |
| DNS failure | FQDN not resolvable | Check DNS configuration, private DNS zones |

#### APIM-Specific Diagnostics

If the request reaches APIM but fails, suggest the user check:
- APIM request logs in Application Insights (correlation ID from response headers)
- What backend URL APIM actually forwarded to
- Whether APIM policies rewrote the request incorrectly
- The `Ocp-Apim-Trace` header for policy execution trace (if enabled)

### Step 7: DNS and Network Checks (if connection fails)

1. Resolve the APIM FQDN: `dig {apim_hostname}` or `nslookup {apim_hostname}`
2. Check if it resolves to a private IP (private endpoint) or public IP
3. Test TCP connectivity: `nc -zv {apim_hostname} 443`
4. If using private endpoints, verify private DNS zone has the correct record

## Output Format

Always provide:

1. **Agent Configuration Summary** — agent name, model property, parsed connection/model
2. **Connection Resource Summary** — target URL, auth type, available models
3. **Model Validation Result** — match/mismatch with details
4. **Replication Test Result** — HTTP status, response body (summarized), latency
5. **Diagnosis** — most likely root cause with confidence level
6. **Remediation Steps** — ordered by likelihood of fixing the issue

## Example Diagnosis Output (Static Models)

```
## BYO Model (APIM) Diagnosis

### Agent: my-agent
- Model property: `grfaoaiapimtest/gpt-5.4`
- Connection: `grfaoaiapimtest` ✅ Found
- Model: `gpt-5.4` ✅ Matches connection metadata

### Connection Details
- Target: `https://grfstandardv2test.azure-api.net/grfaoaiapimtest/openai/v1`
- Auth: ApiKey (default `api-key` header)
- Deployment in path: false
- Discovery: Static (models array present)

### Test Call
- URL: `https://grfstandardv2test.azure-api.net/grfaoaiapimtest/openai/v1/chat/completions`
- Result: HTTP 404
- Response: {"error": "Deployment not found"}

### Diagnosis
The APIM endpoint returned 404. The connection has `deploymentInPath: false`, 
but the APIM backend may expect the deployment name in the URL path.

### Recommended Fix
1. Update the connection metadata to set `deploymentInPath` to `"true"`
2. Or configure the APIM policy to inject the deployment name into the backend URL
```

## Example Diagnosis Output (Dynamic Discovery)

```
## BYO Model (APIM) Diagnosis

### Agent: my-agent
- Model property: `grfaoaiapimtest/gpt-5.4`
- Connection: `grfaoaiapimtest` ✅ Found
- Discovery mode: Dynamic (no static models array)

### Connection Details
- Target: `https://grfstandardv2test.azure-api.net/oldschoolaoai/openai/v1`
- Auth: ApiKey (default `api-key` header)
- Discovery endpoints:
  - List: `GET {target}/deployments`
  - Get: `GET {target}/deployments/{deploymentName}`
  - Provider: AzureOpenAI

### Discovery Test
- URL: `https://grfstandardv2test.azure-api.net/oldschoolaoai/openai/v1/deployments`
- Result: HTTP 200
- Deployments found: gpt-4o, gpt-5.4

### Model Check
- Agent model `gpt-5.4` ✅ Found in discovered deployments

### Diagnosis
Dynamic discovery is working correctly. The agent can discover and reach the model.
If the agent is still failing, check APIM policies on the chat/completions path specifically.
```

## Guardrails

1. Never display full API keys or secrets — mask after first 4 characters
2. Do not modify any resources — this is a read-only diagnostic
3. Separate confirmed facts from hypotheses
4. If a call returns 403, report the permission gap clearly
5. Always show the exact URL and headers (minus secrets) used in the test call so the user can reproduce
