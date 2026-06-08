---
name: foundry-agent-vnet-integration-diagnostics
description: Diagnose networking issues for Azure AI Foundry Agents configured with virtual network (VNet) integration. Traces the network path from a Foundry project or account through injected subnets to their NSGs, and identifies rules that may block agent traffic. Trigger phrases include "foundry agent networking", "agent vnet issues", "why is my agent blocked", "foundry network injection", "agent subnet rules", "agent network diagnostics", "foundry vnet integration".
license: MIT
---

# Foundry Agent VNet Integration Diagnostics

Use this skill when a user needs to trace network policy from a Foundry scope to injected subnets and attached NSGs.

## Goal

Given a Foundry ARM scope (project or account), fetch and correlate:

1. Foundry resource network injection configuration
2. Injected subnet ARM IDs
3. Subnet-level networkSecurityGroup reference
4. NSG inbound and outbound rule posture

## Required Inputs

Read from environment variables — do not ask the user:

1. ARM scope: first of `FOUNDRY_PROJECT_ARM_ID`, `FOUNDRY_PROJECT_RESOURCE_ID`, `AZURE_AI_PROJECT_RESOURCE_ID`, `RBAC_SCOPE_RESOURCE_ID`
2. ARM token: acquire via MSI using `IDENTITY_ENDPOINT` and `IDENTITY_HEADER`:
   `GET {IDENTITY_ENDPOINT}?resource=https://management.azure.com/&api-version=2019-08-01` with header `X-IDENTITY-HEADER: {IDENTITY_HEADER}`

If none of the scope variables are set, stop and ask the user for a full ARM resource ID.

## ARM Data Flow

1. GET the scope resource (project or account)
2. If scope is a project (`.../accounts/{account}/projects/{project}`), derive the account ARM ID by trimming `/projects/{project}`
3. GET the Cognitive Services account resource
4. Read `properties.networkInjections[]` from the account
5. For each item:
   1. Read `scenario`
   2. Read `subnetArmId`
   3. Read `useMicrosoftManagedNetwork`
6. GET each subnet resource from `subnetArmId`
7. Read `properties.networkSecurityGroup.id` on the subnet
8. If NSG exists, GET the NSG resource
9. Summarize:
   1. `properties.securityRules[]`
   2. `properties.defaultSecurityRules[]`
   3. Direction, access, priority, protocol, source, destination, ports

## API Guidance

Use ARM endpoint:

`https://management.azure.com`

Use an ARM token with audience/scope:

`https://management.azure.com/.default`

Recommended API versions:

1. Microsoft.CognitiveServices accounts: `2024-10-01`
2. Microsoft.Network virtualNetworks/subnets: `2024-05-01`
3. Microsoft.Network networkSecurityGroups: `2024-05-01`

If an API version is rejected, retry with the latest stable version available for that provider in the subscription.

## Permission Expectations

Minimum read permissions are required on:

1. Foundry account/project scope
2. Virtual network subnet resource scope
3. NSG resource scope

If any call returns 403, report precisely which resource type failed and that the caller needs read access at that scope.

## Output Format

Always provide:

1. Scope summary
2. Network injections found count
3. Subnet to NSG mapping table
4. NSG rule posture summary
5. High-risk findings

### High-risk findings to call out

1. Inbound allow from `*` or `Internet` to broad destination ports
2. Outbound deny that could block Foundry control/data path
3. Missing NSG where one is expected by policy
4. Conflicting custom rule vs default rule priority

## Example Summary Template

1. Resource scope analyzed
2. Account network injections discovered
3. Per-injection subnet and NSG resolution result
4. NSG rule highlights (allow/deny by direction)
5. Likely network impact to Foundry runtime
6. Suggested least-risk remediation

## Next Steps

If NSG rules look correct but Foundry-configured tools (code interpreter, file search) are still failing on the private network, suggest running the **foundry-agent-vnet-capability-host-diagnostics** skill. That skill validates capability host provisioning and the three required connections (storage, Cosmos DB, AI Search) that the tools/data proxy needs to function on private networks.

## Guardrails

1. Do not modify NSG or subnet resources automatically
2. Do not assume NSG exists; subnet may be unassociated
3. Distinguish missing data (null) from access denied (403)
4. Keep facts separate from hypotheses
