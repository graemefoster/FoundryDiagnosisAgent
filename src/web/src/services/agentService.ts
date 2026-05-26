import { config } from "../config";

export interface ChatMessage {
  role: "user" | "assistant" | "error";
  content: string;
}

export interface StreamResult {
  text: string;
  sessionId: string | null;
}

export interface AgentActivity {
  kind: "tool" | "reasoning" | "intent";
  label: string;
}

export interface SessionFileUploadResult {
  remotePath: string;
  fileName: string;
}

export interface StreamErrorMetadata {
  message: string;
  statusCode?: number;
  errorType?: string;
  model?: string;
  source?: string;
  sessionId?: string | null;
  invocationId?: string;
  providerCode?: string;
  providerMessage?: string;
  recommendation?: string;
}

export class StreamInvocationError extends Error {
  readonly metadata: StreamErrorMetadata;

  constructor(metadata: StreamErrorMetadata) {
    super(metadata.message);
    this.name = "StreamInvocationError";
    this.metadata = metadata;
  }
}

interface ToolRequest {
  name: string;
  arguments?: Record<string, unknown>;
  intentionSummary?: string;
  toolCallId?: string;
  type?: string;
}

interface SessionEventEnvelope {
  type?: string;
  sessionId?: string;
  invocationId?: string;
  fullText?: string;
  message?: string;
  recommendation?: string;
  ephemeral?: boolean;
  detail?: {
    data?: {
      errorType?: string;
      message?: string;
      statusCode?: number;
      model?: string;
      source?: string;
      errorMessage?: string;
    };
  };
  data?: {
    content?: string;
    deltaContent?: string;
    messageId?: string;
    turnId?: string;
    toolRequests?: ToolRequest[];
    reasoningOpaque?: string;
    model?: string;
    statusCode?: number;
    errorMessage?: string;
    source?: string;
    errorType?: string;
    // Ephemeral activity fields
    intent?: string;
    // Tool call routing events
    toolName?: string;
    toolCallId?: string;
    arguments?: Record<string, unknown>;
    // Tool result events
    result?: {
      content?: string;
      detailedContent?: string;
    };
    success?: boolean;
    // Skill metadata
    skills?: Array<{ name?: string; description?: string }>;
    // Permission request events
    permissionRequest?: {
      intention?: string;
      kind?: string;
      toolCallId?: string;
    };
    promptRequest?: {
      intention?: string;
      kind?: string;
      toolCallId?: string;
    };
  };
}

export async function streamMessage(
  accessToken: string,
  messages: ChatMessage[],
  sessionId: string | null,
  onDelta: (delta: string) => void,
  onActivity?: (activity: AgentActivity) => void
): Promise<StreamResult> {
  const lastUser = messages.findLast((message) => message.role === "user");
  if (!lastUser) {
    throw new Error("No user message");
  }

  const requestUrl = new URL(config.agentBaseUrl);
  if (sessionId) {
    requestUrl.searchParams.set("agent_session_id", sessionId);
  }

  const response = await fetch(requestUrl, {
    method: "POST",
    headers: {
      "Authorization": `Bearer ${accessToken}`,
      "Content-Type": "application/json",
      "Foundry-Features": "HostedAgents=V1Preview",
    },
    body: JSON.stringify({ input: lastUser.content }),
  });

  if (!response.ok) {
    const errorText = await response.text();
    throw new Error(errorText || `Request failed with status ${response.status}`);
  }

  if (!response.body) {
    throw new Error("Streaming response body was unavailable");
  }

  const responseSessionId = response.headers.get("x-agent-session-id");
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  let deltaText = "";
  let finalText = "";
  let activeSessionId = responseSessionId ?? sessionId;
  let latestErrorMetadata: Partial<StreamErrorMetadata> = {};

  while (true) {
    const { done, value } = await reader.read();
    buffer += decoder.decode(value ?? new Uint8Array(), { stream: !done });

    let boundaryIndex = buffer.indexOf("\n\n");
    while (boundaryIndex >= 0) {
      const rawEvent = buffer.slice(0, boundaryIndex);
      buffer = buffer.slice(boundaryIndex + 2);

      const payload = parseSsePayload(rawEvent);
      if (payload) {
        activeSessionId = payload.sessionId ?? activeSessionId;
        latestErrorMetadata = mergeErrorMetadata(latestErrorMetadata, payload, activeSessionId);

        if (payload.type === "done") {
          return {
            text: payload.fullText ?? (finalText || deltaText),
            sessionId: activeSessionId,
          };
        }

        if (payload.type === "error") {
          throw toStreamInvocationError(payload, latestErrorMetadata, activeSessionId);
        }

        // Invocations protocol: delta events have data.deltaContent (no type field)
        const delta = payload.data?.deltaContent ?? "";
        if (delta) {
          deltaText += delta;
          onDelta(delta);
        }

        // Tool requests signal the agent is "working"
        if (payload.data?.toolRequests?.length) {
          for (const tool of payload.data.toolRequests) {
            if (tool.name === "report_intent") {
              const intent = tool.arguments?.intent as string | undefined;
              if (intent) {
                onActivity?.({ kind: "intent", label: intent });
              }
            } else {
              const label = tool.intentionSummary
                ?? (tool.arguments?.description as string | undefined)
                ?? tool.name;
              onActivity?.({ kind: "tool", label });
            }
          }
        }

        // Intent events (separate from tool requests)
        if (payload.data?.intent) {
          onActivity?.({ kind: "intent", label: payload.data.intent });
        }

        // Tool-call routing events (bash, skill, etc.)
        if (payload.data?.toolName && payload.data?.arguments) {
          const toolName = payload.data.toolName;
          const args = payload.data.arguments;
          if (toolName === "bash" && args.description) {
            onActivity?.({ kind: "tool", label: `${args.description}` });
          } else if (toolName === "skill" && args.skill) {
            onActivity?.({ kind: "tool", label: `Loading skill: ${args.skill}` });
          } else if (toolName !== "report_intent") {
            const label = (args.description as string) ?? (args.intentionSummary as string) ?? toolName;
            onActivity?.({ kind: "tool", label });
          }
        }

        // Reasoning indicator
        if (payload.data?.reasoningOpaque) {
          onActivity?.({ kind: "reasoning", label: "Thinking…" });
        }

        // Tool result events show what the agent found
        if (payload.data?.result?.content && payload.data?.toolCallId) {
          const resultSummary = payload.data.result.content.length > 100
            ? payload.data.result.content.slice(0, 100) + "…"
            : payload.data.result.content;
          onActivity?.({ kind: "tool", label: `✓ ${resultSummary}` });
        }

        // Final turn message has data.content + data.turnId (no type field)
        if (payload.data?.content && payload.data?.turnId) {
          finalText = payload.data.content;
        }
      }

      boundaryIndex = buffer.indexOf("\n\n");
    }

    if (done) {
      break;
    }
  }

  return {
    text: finalText || deltaText,
    sessionId: activeSessionId,
  };
}

function toStreamInvocationError(
  payload: SessionEventEnvelope,
  latest: Partial<StreamErrorMetadata>,
  activeSessionId: string | null
): StreamInvocationError {
  const detail = payload.detail?.data;
  const detailMessage = detail?.message;
  const rootMessage = payload.message;
  const providerRaw = detail?.errorMessage ?? payload.data?.errorMessage;
  const providerParsed = parseProviderError(providerRaw);

  return new StreamInvocationError({
    message:
      detailMessage ??
      rootMessage ??
      latest.message ??
      providerParsed.providerMessage ??
      "The agent returned an error",
    statusCode: detail?.statusCode ?? payload.data?.statusCode ?? latest.statusCode,
    errorType: detail?.errorType ?? payload.data?.errorType ?? latest.errorType,
    model: detail?.model ?? payload.data?.model ?? latest.model,
    source: detail?.source ?? payload.data?.source ?? latest.source,
    sessionId: payload.sessionId ?? activeSessionId ?? latest.sessionId,
    invocationId: payload.invocationId ?? latest.invocationId,
    providerCode: providerParsed.providerCode ?? latest.providerCode,
    providerMessage: providerParsed.providerMessage ?? latest.providerMessage,
    recommendation: payload.recommendation ?? latest.recommendation,
  });
}

function mergeErrorMetadata(
  latest: Partial<StreamErrorMetadata>,
  payload: SessionEventEnvelope,
  activeSessionId: string | null
): Partial<StreamErrorMetadata> {
  const providerRaw = payload.data?.errorMessage ?? payload.detail?.data?.errorMessage;
  const providerParsed = parseProviderError(providerRaw);

  return {
    ...latest,
    message: payload.detail?.data?.message ?? latest.message,
    statusCode: payload.data?.statusCode ?? payload.detail?.data?.statusCode ?? latest.statusCode,
    errorType: payload.data?.errorType ?? payload.detail?.data?.errorType ?? latest.errorType,
    model: payload.data?.model ?? payload.detail?.data?.model ?? latest.model,
    source: payload.data?.source ?? payload.detail?.data?.source ?? latest.source,
    sessionId: payload.sessionId ?? activeSessionId ?? latest.sessionId,
    invocationId: payload.invocationId ?? latest.invocationId,
    providerCode: providerParsed.providerCode ?? latest.providerCode,
    providerMessage: providerParsed.providerMessage ?? latest.providerMessage,
    recommendation: payload.recommendation ?? latest.recommendation,
  };
}

function parseProviderError(raw: string | undefined): {
  providerCode?: string;
  providerMessage?: string;
} {
  if (!raw) {
    return {};
  }

  try {
    const parsed = JSON.parse(raw) as { code?: string; message?: string };
    return {
      providerCode: parsed.code,
      providerMessage: parsed.message,
    };
  } catch {
    return { providerMessage: raw };
  }
}

function parseSsePayload(rawEvent: string): SessionEventEnvelope | null {
  const dataLines = rawEvent
    .split("\n")
    .filter((line) => line.startsWith("data:"))
    .map((line) => line.slice(5).trimStart());

  if (dataLines.length === 0) {
    return null;
  }

  try {
    return JSON.parse(dataLines.join("\n")) as SessionEventEnvelope;
  } catch {
    return null;
  }
}

export async function uploadSessionFile(
  accessToken: string,
  sessionId: string,
  remotePath: string,
  file: File
): Promise<SessionFileUploadResult> {
  if (!remotePath.trim()) {
    throw new Error("A destination path is required.");
  }

  const endpoint = buildSessionFileContentEndpoint(sessionId, remotePath.trim());

  const response = await fetch(endpoint, {
    method: "PUT",
    headers: {
      "Authorization": `Bearer ${accessToken}`,
      "Foundry-Features": "HostedAgents=V1Preview",
      "Content-Type": "application/octet-stream",
    },
    body: file,
  });

  if (!response.ok) {
    const errorText = await response.text();
    throw new Error(errorText || `File upload failed with status ${response.status}`);
  }

  return {
    remotePath: remotePath.trim(),
    fileName: file.name,
  };
}

function buildSessionFileContentEndpoint(sessionId: string, remotePath: string): string {
  const configuredEndpoint = config.agentFilesBaseUrl;
  if (configuredEndpoint) {
    const directUrl = new URL(configuredEndpoint);
    directUrl.pathname = `${directUrl.pathname.replace(/\/$/, "")}/${encodeURIComponent(sessionId)}/files/content`;
    directUrl.searchParams.set("path", remotePath);
    ensureApiVersion(directUrl);
    return directUrl.toString();
  }

  const invocationsUrl = new URL(config.agentBaseUrl);
  invocationsUrl.pathname = invocationsUrl.pathname.replace(
    /\/endpoint\/protocols\/invocations\/?$/,
    `/endpoint/sessions/${encodeURIComponent(sessionId)}/files/content`
  );
  invocationsUrl.searchParams.set("path", remotePath);
  ensureApiVersion(invocationsUrl);
  return invocationsUrl.toString();
}

function ensureApiVersion(url: URL): void {
  const configuredApiVersion = config.agentFilesApiVersion;
  if (configuredApiVersion) {
    url.searchParams.set("api-version", configuredApiVersion);
    return;
  }

  if (!url.searchParams.has("api-version")) {
    url.searchParams.set("api-version", "2025-11-15-preview");
  }
}
