#!/usr/bin/env node

import { createServer } from "node:http";
import { readFileSync, existsSync, statSync } from "node:fs";
import { resolve, join, extname } from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = fileURLToPath(new URL(".", import.meta.url));
const distDir = resolve(__dirname, "..", "dist");

// Parse CLI arguments
const args = process.argv.slice(2);
const flags = {};

for (let i = 0; i < args.length; i++) {
  if (args[i] === "--help" || args[i] === "-h") {
    printHelp();
    process.exit(0);
  }
  if (args[i].startsWith("--") && i + 1 < args.length) {
    const key = args[i].slice(2);
    flags[key] = args[++i];
  }
}

const port = parseInt(flags["port"] ?? "3000", 10);
const agentUrl = flags["agent-url"];
const clientId = flags["client-id"];
const authority = flags["authority"];
const token = flags["token"];

if (!agentUrl) {
  console.error("Error: --agent-url is required");
  console.error('Example: npx @graemefoster/foundry-sentinel --agent-url "https://your-foundry.services.ai.azure.com/api/projects/proj-default/agents/my-agent/endpoint/protocols/invocations?api-version=v1"');
  process.exit(1);
}

if (!token && !clientId) {
  console.error("Error: Either --client-id (for OIDC sign-in) or --token (for direct access) is required");
  process.exit(1);
}

// Build the runtime config to inject
const runtimeConfig = {
  agentBaseUrl: agentUrl,
  msalClientId: clientId ?? "",
  msalAuthority: authority ?? "https://login.microsoftonline.com/common",
  foundryAccessToken: token ?? undefined,
};

const configScript = `<script>window.__SENTINEL_CONFIG__ = ${JSON.stringify(runtimeConfig)};</script>`;

// MIME types
const mimeTypes = {
  ".html": "text/html",
  ".js": "application/javascript",
  ".css": "text/css",
  ".json": "application/json",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".ico": "image/x-icon",
  ".woff": "font/woff",
  ".woff2": "font/woff2",
};

if (!existsSync(distDir)) {
  console.error(`Error: dist directory not found at ${distDir}`);
  console.error("The package may not have been built correctly.");
  process.exit(1);
}

const server = createServer((req, res) => {
  let urlPath = new URL(req.url, `http://localhost:${port}`).pathname;

  // SPA fallback: serve index.html for non-file paths
  let filePath = join(distDir, urlPath);

  if (!existsSync(filePath) || statSync(filePath).isDirectory()) {
    // Check for auth.html
    if (urlPath === "/auth.html") {
      filePath = join(distDir, "auth.html");
    } else {
      filePath = join(distDir, "index.html");
    }
  }

  if (!existsSync(filePath)) {
    res.writeHead(404);
    res.end("Not Found");
    return;
  }

  const ext = extname(filePath);
  const contentType = mimeTypes[ext] ?? "application/octet-stream";

  let content = readFileSync(filePath);

  // Inject runtime config into HTML pages
  if (ext === ".html") {
    const html = content.toString("utf-8");
    content = Buffer.from(html.replace("</head>", `${configScript}\n</head>`));
  }

  res.writeHead(200, { "Content-Type": contentType });
  res.end(content);
});

server.listen(port, () => {
  console.log(`\n  FoundrySentinel is running at http://localhost:${port}\n`);
  console.log(`  Agent URL: ${agentUrl}`);
  if (token) {
    console.log("  Auth: Using provided access token");
  } else {
    console.log(`  Auth: OIDC (client: ${clientId})`);
  }
  console.log("");
});

function printHelp() {
  console.log(`
  FoundrySentinel — Foundry Diagnostics Terminal

  Usage:
    npx @graemefoster/foundry-sentinel [options]

  Required:
    --agent-url <url>     Foundry agent invocations endpoint URL

  Authentication (one required):
    --client-id <id>      MSAL client ID for OIDC sign-in
    --token <jwt>         Direct access token (skips sign-in)

  Optional:
    --authority <url>     MSAL authority URL (default: https://login.microsoftonline.com/common)
    --port <number>       Port to serve on (default: 3000)

  Examples:
    # With OIDC sign-in
    npx @graemefoster/foundry-sentinel \\
      --agent-url "https://myfoundry.services.ai.azure.com/api/projects/proj-default/agents/my-agent/endpoint/protocols/invocations?api-version=v1" \\
      --client-id "1ca742d5-84bf-4e50-8b16-5dea5938b13c" \\
      --authority "https://login.microsoftonline.com/my-tenant-id"

    # With direct token
    npx @graemefoster/foundry-sentinel \\
      --agent-url "https://myfoundry.services.ai.azure.com/api/projects/proj-default/agents/my-agent/endpoint/protocols/invocations?api-version=v1" \\
      --token "$(az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv)"
`);
}
