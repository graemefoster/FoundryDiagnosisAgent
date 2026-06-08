---
name: foundry-agent-vnet-capability-host-diagnostics
description: Diagnose VNet integration setup issues for Azure AI Foundry Agents by validating network injections, capability host configuration, and required connections. Checks that the account capability host was provisioned correctly (not manually created), and that the project capability host has all three required connections (storage, cosmos, search) needed for the tools/data proxy to function on private networks. Trigger phrases include "capability host", "vnet setup", "tools proxy not working", "agent tools broken on private network", "network injection setup", "capability host connections", "foundry vnet capability host", "agent private network tools".
license: MIT
---

# Foundry Agent VNet Capability Host Diagnostics

Use this skill when a user has connected their Foundry agents to a virtual network and tools are not working, or when validating that VNet integration setup is correct. This checks the *setup correctness* of network injections and capability hosts — not NSG rules (see `foundry-agent-vnet-integration-diagnostics` for NSG analysis).

## Background

When Foundry agents are configured with VNet integration, the agent can see and directly call endpoints on the private network. However, Foundry-configured tools (e.g. code interpreter, file search) do **not** call targets directly — they route through a **tools/data proxy** inside the Foundry environment. This proxy requires specific capability host configuration and connections to function on private networks.

Key facts:
- The **account-level capability host** must be provisioned by the Foundry resource provider (not manually created)
- The **project-level capability host** must have three connections configured: storage, Cosmos DB (thread storage), and AI Search (vector store)
- If any of these are missing, the tools proxy will not function on private networks — even though the agent itself can reach private endpoints directly

## Required Inputs

Ask the user for:

1. **Project ARM Resource ID** — full ARM path, e.g.:
   `/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.CognitiveServices/accounts/{account}/projects/{project}`

The skill derives all other identifiers (subscription, resource group, account name, project name) from this path.

If running inside the hosted agent, check these environment variables first (in order): `FOUNDRY_PROJECT_ARM_ID`, `FOUNDRY_PROJECT_RESOURCE_ID`, `AZURE_AI_PROJECT_RESOURCE_ID`. If any is set, use it without asking the user.

## Token Acquisition

Check `IDENTITY_ENDPOINT` first:

**Hosted (MSI available):**
```
GET {IDENTITY_ENDPOINT}?resource=https://management.azure.com/&api-version=2019-08-01
X-IDENTITY-HEADER: {IDENTITY_HEADER}
```

**Local development (no IDENTITY_ENDPOINT):**
```bash
az account get-access-token --resource https://management.azure.com/ --query accessToken -o tsv
```

## Diagnostic Workflow

### Step 1: Validate Network Injections on the Foundry Account

**Purpose:** Confirm the account is configured for VNet integration and identify the injected subnet.

1. Derive the account ARM ID by stripping `/projects/{project}` from the project ARM ID
2. GET the account resource:
   ```
   GET https://management.azure.com/{accountArmId}?api-version=2025-09-01
   Authorization: Bearer {token}
   ```
3. Read `properties.networkInjections[]`
4. For each injection, note:
   - `scenario` — what the injection is for
   - `subnetArmId` — the subnet the agents are injected into
   - `useMicrosoftManagedNetwork` — whether Microsoft manages the network

**Pass criteria:**
- At least one network injection exists
- Each injection has a valid `subnetArmId`

**Fail criteria:**
- `properties.networkInjections` is null or empty → VNet integration is not configured on this account
- `subnetArmId` is null or malformed → injection is misconfigured

### Step 2: Validate Account-Level Capability Host

**Purpose:** Confirm the account capability host was provisioned by the Foundry resource provider (not manually created by the customer).

1. GET the account capability hosts:
   ```
   GET https://management.azure.com/{accountArmId}/capabilityHosts/?api-version=2025-09-01
   Authorization: Bearer {token}
   ```
2. Check for a capability host with name matching the pattern: `{accountName}@aml_aiagentservice`
   - Extract `{accountName}` from the account ARM ID (the segment after `/accounts/`)

**Pass criteria:**
- A capability host exists with name exactly matching `{accountName}@aml_aiagentservice`
- Its `provisioningState` is `Succeeded`

**Fail criteria:**
- No capability hosts found → account not set up for agents with VNet integration
- Capability host exists but name does NOT match `{accountName}@aml_aiagentservice` → the customer likely created this manually. Manually-created capability hosts do not work correctly with VNet integration. The Foundry resource provider must create it.

**Warning criteria:**
- `provisioningState` is not `Succeeded` → provisioning may have failed or be in progress. Proceed to Step 2b to investigate.

#### Step 2b: Activity Log Investigation (only if provisioningState is Failed)

**Purpose:** When a capability host has a failed `provisioningState`, query the Azure Activity Log to find the actual error details.

1. Query the Activity Log for the Foundry account resource, filtering for capability host operations:
   ```
   GET https://management.azure.com/subscriptions/{subscriptionId}/providers/microsoft.insights/eventtypes/management/values?api-version=2017-03-01-preview&$filter=eventTimestamp ge '{startTime}' and eventTimestamp le '{endTime}' and eventChannels eq 'Admin, Operation' and resourceGroupName eq '{resourceGroup}' and resourceId eq '{accountArmId}' and levels eq 'Critical,Error,Warning,Informational' and searchText eq 'capabilityhost'
   Authorization: Bearer {token}
   ```

2. Time range selection:
   - Default: search the last 24 hours
   - If no results found, widen to 7 days
   - If still no results, ask the user when they created or last modified the capability host to narrow the search window

3. Look for entries with:
   - `operationName` containing `capabilityHosts`
   - `status` of `Failed`
   - `properties.statusMessage` — this contains the actual error detail

**Findings to report:**
- The operation that failed and its timestamp
- The error message from `properties.statusMessage`
- Any correlation ID that can be used for support escalation

**If no activity log entries found:**
- Note that the logs may have aged out (Activity Log retains 90 days)
- Ask the user if they know approximately when the capability host was created/modified
- Recommend checking the Azure Portal Activity Log with the filter `Resource: {accountName}` and operation containing "capabilityHost"

### Step 3: Validate Project-Level Capability Host Connections

**Purpose:** Confirm the project capability host has all three connections required for the tools/data proxy to function on private networks.

1. GET the project capability hosts:
   ```
   GET https://management.azure.com/{projectArmId}/capabilityHosts/?api-version=2025-09-01
   Authorization: Bearer {token}
   ```
2. For each capability host with `capabilityHostKind: "Agents"`, check for:
   - `storageConnections` — connection to Azure Storage (required)
   - `threadStorageConnections` — connection to Cosmos DB where agent conversations are stored (required)
   - `vectorStoreConnections` — connection to Azure AI Search (required)

**Pass criteria:**
- All three connection arrays are present and non-empty

**Fail criteria:**
- Any of the three connection arrays is null or empty → the tools/data proxy will NOT function on the private network

**If provisioningState is Failed:** Follow the same Activity Log investigation as Step 2b, but use the project ARM ID as the `resourceId` filter and search for `capabilityhost`.

**Explanation to provide on failure:**
> Your Foundry agent can see the private network and call endpoints directly. However, Foundry-configured tools (code interpreter, file search, etc.) route through a tools/data proxy. This proxy requires storage, Cosmos DB, and AI Search connections to be configured on the project capability host. Without all three, tools will fail even though the agent has network connectivity.

### Step 4: Summary & Recommendations

Present results as a checklist:

```
## VNet Capability Host Diagnostic Results

| Check | Status | Detail |
|-------|--------|--------|
| Network injections configured | ✅/❌ | {subnet or error} |
| Account capability host exists | ✅/❌ | {name found or missing} |
| Account capability host naming correct | ✅/❌/⚠️ | {name vs expected pattern} |
| Account capability host provisioning | ✅/❌ | {Succeeded or Failed + activity log error} |
| Project capability host exists | ✅/❌ | {name or missing} |
| Storage connection | ✅/❌ | {connection name or missing} |
| Thread storage connection (Cosmos) | ✅/❌ | {connection name or missing} |
| Vector store connection (AI Search) | ✅/❌ | {connection name or missing} |
```

For each failure, provide:
1. What is wrong
2. Why it matters
3. How to fix it (least disruptive action)

## Common Issues & Remediation

| Issue | Root Cause | Fix |
|-------|-----------|-----|
| No network injections | VNet integration not enabled on account | Enable VNet integration in the Foundry account network settings |
| Account capability host wrong name | Customer manually created the capability host | Delete the manual capability host and re-provision via Foundry (the RP must create it) |
| Account capability host Failed | Provisioning failed (check activity log for details) | Delete and recreate — see recovery procedure below |
| Project capability host Failed | Provisioning failed (check activity log for details) | Delete and recreate — see recovery procedure below |
| Missing storage/cosmos/search connections | Project capability host created without required connections | Update the project capability host to include all three connection references |
| Tools work without VNet but break with VNet | Proxy cannot function without the three connections on private networks | Add the missing connections to the project capability host |

## ⚠️ Failed Capability Host Recovery

**Critical: Failed capability hosts are NOT recoverable. They must be deleted and recreated.**

Deletion must follow a specific order — you cannot delete the account capability host while a project capability host still exists.

### Deletion Order

1. **Delete the project capability host FIRST** (if it exists):
   ```
   DELETE https://management.azure.com/{projectArmId}/capabilityHosts/{capHostName}?api-version=2025-09-01
   Authorization: Bearer {token}
   Content-Type: application/json
   Body: {}
   ```

2. **Then delete the account capability host**:
   ```
   DELETE https://management.azure.com/{accountArmId}/capabilityHosts/{capHostName}?api-version=2025-09-01
   Authorization: Bearer {token}
   Content-Type: application/json
   Body: {}
   ```

### az CLI equivalent

```bash
# Step 1: Delete project capability host
az rest --method delete \
  --url "https://management.azure.com/{projectArmId}/capabilityHosts/{capHostName}?api-version=2025-09-01" \
  --body '{}'

# Step 2: Delete account capability host
az rest --method delete \
  --url "https://management.azure.com/{accountArmId}/capabilityHosts/{capHostName}?api-version=2025-09-01" \
  --body '{}'
```

### After Deletion

After deleting both capability hosts, the user must re-provision them through the Foundry portal or ARM template. The Foundry resource provider must create the account capability host (manual creation results in the wrong naming pattern and does not work).

## API Reference

| Resource | API Version | Path |
|----------|-------------|------|
| Foundry Account | `2025-09-01` | `Microsoft.CognitiveServices/accounts/{account}` |
| Account Capability Hosts | `2025-09-01` | `Microsoft.CognitiveServices/accounts/{account}/capabilityHosts/` |
| Project Capability Hosts | `2025-09-01` | `Microsoft.CognitiveServices/accounts/{account}/projects/{project}/capabilityHosts/` |
| Activity Log | `2017-03-01-preview` | `microsoft.insights/eventtypes/management/values` (subscription-level) |

## Permission Expectations

Minimum read permissions required on:
1. Foundry account resource
2. Foundry project resource
3. Capability host sub-resources

If any call returns 403, report which resource type failed and that the caller needs `Reader` or `Cognitive Services User` access at that scope.

## Next Steps

If capability host setup looks correct but the agent still cannot reach endpoints on the private network, suggest running the **foundry-agent-vnet-integration-diagnostics** skill. That skill traces the network path from the Foundry account through injected subnets to their NSGs and identifies rules that may be blocking agent traffic.

## Guardrails

1. Do not create, modify, or delete capability hosts or connections — recommend changes only
2. Distinguish between missing data (null/empty) and access denied (403)
3. If the account capability host name is wrong, strongly flag this as a likely root cause
4. Keep facts separate from hypotheses — be clear about what was observed vs what is inferred
