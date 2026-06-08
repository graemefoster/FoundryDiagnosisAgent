<div align="center">

# 🛡️ Foundry Sentinel

**Network & Integration Diagnostics Agent for Microsoft Foundry**

[![Publish Agent Image](https://github.com/graemefoster/FoundryDiagnosisAgent/actions/workflows/publish-agent.yml/badge.svg)](https://github.com/graemefoster/FoundryDiagnosisAgent/actions/workflows/publish-agent.yml)
[![Publish Web Package](https://github.com/graemefoster/FoundryDiagnosisAgent/actions/workflows/publish-web.yml/badge.svg)](https://github.com/graemefoster/FoundryDiagnosisAgent/actions/workflows/publish-web.yml)

A hosted Foundry agent that diagnoses network connectivity, DNS resolution, TLS, and integration issues — directly from inside your Foundry environment. Chat with it through a polished web UI served via a single `npx` command.

</div>

---

## ✨ What It Does

Foundry Sentinel runs **inside Microsoft Foundry** as a hosted agent with access to real network diagnostics tooling:

- 🔍 **DNS resolution** — dig, nslookup

- 🌐 **TCP connectivity** — traceroute

- 🔒 **TLS inspection** — certificate chains, expiry, SNI issues

- 📊 **Network analysis** — latency measurements, path visualization

Ask it things like:
> *"Can you check if my-service.internal:443 is reachable and show the TLS certificate chain?"* 
>
> *"My BYO Model connection isn't working - what might be wrong with it?"*
>
> *"Can you trace the call to mymcptool.internal.net"*

---

## 🚀 Quick Start

### 1. Deploy the Agent

#### Pull the Docker image

The agent image is published to GitHub Container Registry:

```bash
docker pull ghcr.io/graemefoster/foundry-diagnostics-agent:latest
```

#### Import into your Azure Container Registry

> ⚠️ **Foundry hosted agents currently require a publicly accessible ACR.** Import the image into your own ACR:

**Linux / macOS:**
```bash
az acr import \
  --name <your-acr-name> \
  --source ghcr.io/graemefoster/foundry-diagnostics-agent:latest \
  --image foundry-diagnostics-agent:latest
```

**Windows (PowerShell):**
```powershell
az acr import `
  --name <your-acr-name> `
  --source ghcr.io/graemefoster/foundry-diagnostics-agent:latest `
  --image foundry-diagnostics-agent:latest
```

#### Register the agent with Foundry

Use the [infra/CreateAgent.http](infra/CreateAgent.http) file with the VS Code REST Client extension:

1. Get a fresh Foundry token:
   ```bash
   ./infra/refresh-foundry-token.sh
   ```

2. Update `CreateAgent.http` with your ACR image path and Foundry project details

3. Send the POST request to register the agent

The agent definition looks like:
```json
{
  "name": "diagnostics-agent",
  "definition": {
    "kind": "hosted",
    "container_protocol_versions": [
      { "protocol": "invocations", "version": "1.0.0" }
    ],
    "cpu": "0.5",
    "memory": "1Gi",
    "image": "<your-acr>.azurecr.io/foundry-diagnostics-agent:latest",
    "environment_variables": {
      "AZURE_AI_MODEL_DEPLOYMENT_NAME": "gpt-5.4"
    }
  },
  "description": "A Foundry Network and Integration Diagnostics Agent"
}
```

---

### 2. Run the Web UI

The web UI is a single `npx` command — no install required:

**Linux / macOS:**
```bash
# Option A: With OIDC sign-in (recommended for teams)
npx @graemefoster/foundry-sentinel \
  --agent-url "https://<foundry>.services.ai.azure.com/api/projects/<project>/agents/<agent-name>/endpoint/protocols/invocations?api-version=v1" \
  --client-id "<your-entra-app-client-id>" \
  --authority "https://login.microsoftonline.com/<your-tenant-id>"

# Option B: With a direct access token (quick local testing)
npx @graemefoster/foundry-sentinel \
  --agent-url "https://<foundry>.services.ai.azure.com/api/projects/<project>/agents/<agent-name>/endpoint/protocols/invocations?api-version=v1" \
  --token "$(az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv)"
```

**Windows (PowerShell):**
```powershell
# Option A: With OIDC sign-in (recommended for teams)
npx @graemefoster/foundry-sentinel `
  --agent-url "https://<foundry>.services.ai.azure.com/api/projects/<project>/agents/<agent-name>/endpoint/protocols/invocations?api-version=v1" `
  --client-id "<your-entra-app-client-id>" `
  --authority "https://login.microsoftonline.com/<your-tenant-id>"

# Option B: With a direct access token (quick local testing)
$token = az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv
npx @graemefoster/foundry-sentinel `
  --agent-url "https://<foundry>.services.ai.azure.com/api/projects/<project>/agents/<agent-name>/endpoint/protocols/invocations?api-version=v1" `
  --token $token
```

Then open **http://localhost:3000** in your browser.

#### CLI Options

| Flag | Required | Description |
|------|----------|-------------|
| `--agent-url` | ✅ | Foundry agent invocations endpoint URL (e.g. https://<foundry>.services.ai.azure.com/api/projects/<project>/agents/<agent-name>/endpoint/protocols/invocations?api-version=v1") |
| `--client-id` | One of | Entra app client ID for OIDC sign-in |
| `--token` | One of | Direct access token (skips sign-in UI - az account get-access-token --resource https://ai.azure.com) |
| `--authority` | No | MSAL authority (default: `https://login.microsoftonline.com/common`) |
| `--port` | No | Port to serve on (default: `3000`) |

> 💡 **Tip**: The web UI also has a built-in token paste option. If you choose OIDC mode, you can still sign in by pasting a token directly — the UI shows the `az` command with a copy button.

---

## 🏗️ Architecture

```
┌─────────────────────────────────────────────────────┐
│  Your Browser (npx @graemefoster/foundry-sentinel)  │
└──────────────────────────┬──────────────────────────┘
                           │ HTTPS (Invocations protocol)
                           ▼
┌──────────────────────────────────────────────────────┐
│              Microsoft Foundry                       │
│  ┌────────────────────────────────────────────────┐  │
│  │         Virtual Network (optional)             │  │
│  │  ┌──────────────────────────────────────────┐  │  │
│  │  │ Foundry Sentinel Agent (hosted container)│  │  │
│  │  │  • .NET 10 + GitHub Copilot SDK          │  │  │
│  │  │  • Python diagnostics tooling            │  │  │
│  │  │  • Network utilities (dig, mtr)          │  │  │
│  │  └──────────────────────────────────────────┘  │  │
│  │                                                │  │
│  │  ← Can reach private endpoints in your VNet    │  │
│  └────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────┘
```

---

## 🔧 Development

### Agent (backend)

```bash
cd src/agent
dotnet run
```

### Web UI (frontend)

```bash
cd src/web
npm install
npm run dev
```

Create a `.env.local` with your Foundry details. You can authenticate via MSAL (interactive sign-in) **or** a pre-obtained JWT:

**Option A — MSAL sign-in:**
```env
VITE_AGENT_BASE_URL=https://<foundry>.services.ai.azure.com/api/projects/<project>/agents/<agent>/endpoint/protocols/invocations?api-version=v1
VITE_MSAL_CLIENT_ID=<your-client-id>
VITE_MSAL_AUTHORITY=https://login.microsoftonline.com/<tenant-id>
```

**Option B — Direct access token (skips sign-in):**
```env
VITE_AGENT_BASE_URL=https://<foundry>.services.ai.azure.com/api/projects/<project>/agents/<agent>/endpoint/protocols/invocations?api-version=v1
VITE_FOUNDRY_ACCESS_TOKEN=<your-jwt>
```

> **Tip:** Generate a token with:
> ```bash
> az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv
> ```

---

## 📦 CI/CD

| Workflow | Trigger | Publishes |
|----------|---------|-----------|
| [publish-web.yml](.github/workflows/publish-web.yml) | Push to `main` (`src/web/**`) | npm package → GitHub Packages |
| [publish-agent.yml](.github/workflows/publish-agent.yml) | Push to `main` (`src/agent/**`) | Docker image → ghcr.io |

Both workflows also support `workflow_dispatch` for manual runs.

---

## 📄 License

MIT

---

<div align="center">
<sub>Built with ❤️ for Microsoft Foundry</sub>
</div>
