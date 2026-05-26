import { useEffect, useRef, useState } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { useAuth } from "./AuthContext";
import { APP_VERSION } from "./config";
import {
  type ChatMessage,
  type AgentActivity,
  StreamInvocationError,
  streamMessage,
  uploadSessionFile,
} from "./services/agentService";
import "./App.css";

const AGENT_NAME = "FoundrySentinel";
const AGENT_SUBTITLE = "Foundry Diagnostics Terminal";
const AZ_TOKEN_COMMAND = "az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv";

interface PendingAttachment {
  id: string;
  file: File;
  remotePath: string;
  mention: string;
  uploaded: boolean;
}

interface ChatErrorState {
  summary: string;
  details?: string;
}

function withSessionBanner(content: string): string {
  const banner = [
    "```text",
    " _____ ___  _   _ _  _ ___  _____   __  ___ ___ _  _ _____ ___ _  _ ___ _    ",
    "|  ___/ _ \\| | | | \\| |   \\| _ \\ \\ / / / __| __| \\| |_   _|_ _| \\| | __| |   ",
    "| |_ | (_) | |_| | .` | |) |   /\\ V /  \\__ \\ _|| .` | | |  | || .` | _|| |__ ",
    "|_|   \\___/ \\___/|_|\\_|___/|_|_\\ |_|   |___/___|_|\\_| |_| |___|_|\\_|___|____|",
    "```",
  ].join("\n");

  return `${banner}\n\n${content}`;
}

function buildErrorState(err: unknown, activeSessionId: string | null): ChatErrorState {
  if (err instanceof StreamInvocationError) {
    const metadata = err.metadata;
    const lines: string[] = [];

    lines.push("## Request Failed");
    lines.push("");
    if (metadata.message) {
      lines.push(metadata.message);
      lines.push("");
    }

    if (metadata.recommendation) {
      lines.push("> **Recommendation:** " + metadata.recommendation);
      lines.push("");
    }

    // Details table
    const details: [string, string][] = [
      ["Provider code", metadata.providerCode],
      ["Provider detail", metadata.providerMessage],
      ["Error type", metadata.errorType],
      ["HTTP status", metadata.statusCode ? String(metadata.statusCode) : ""],
      ["Model", metadata.model],
      ["Source", metadata.source],
      ["Session", metadata.sessionId || activeSessionId || "not established"],
      ["Invocation", metadata.invocationId],
    ].filter(([, v]) => !!v) as [string, string][];

    if (details.length > 0) {
      lines.push("| Field | Value |");
      lines.push("|-------|-------|");
      for (const [label, value] of details) {
        lines.push(`| ${label} | \`${value}\` |`);
      }
      lines.push("");
    }

    lines.push("> **Tip:** for auth/permission failures, assign the `Foundry User` role (or equivalent data-plane actions) and verify provider credentials.");

    return {
      summary: "Request failed",
      details: lines.join("\n"),
    };
  }

  const message = err instanceof Error ? err.message : "Unknown error";
  const lines = [
    "## Request Failed",
    "",
    message,
    "",
    `| Field | Value |`,
    `|-------|-------|`,
    `| Session | \`${activeSessionId || "not established"}\` |`,
    "",
    "> **Tip:** inspect agent logs for invocation and tool-call failures.",
  ];

  return {
    summary: "Request failed",
    details: lines.join("\n"),
  };
}

function App() {
  const { isAuthenticated, userName, getAccessToken, signIn, signOut, signInWithToken } = useAuth();
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [input, setInput] = useState("");
  const [streamingText, setStreamingText] = useState<string | null>(null);
  const [activities, setActivities] = useState<AgentActivity[]>([]);
  const [loading, setLoading] = useState(false);
  const [uploading, setUploading] = useState(false);
  const [pendingAttachments, setPendingAttachments] = useState<PendingAttachment[]>([]);
  const [uploadMessage, setUploadMessage] = useState<string | null>(null);
  const [sessionId, setSessionId] = useState<string | null>(null);
  const bottomRef = useRef<HTMLDivElement>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const composerRef = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    // Keep the latest streamed/message content in view.
    bottomRef.current?.scrollIntoView({ behavior: "auto" });
  }, [messages, streamingText, activities]);

  useEffect(() => {
    const composer = composerRef.current;
    if (!composer) {
      return;
    }

    const maxComposerHeight = 160;
    composer.style.height = "auto";
    composer.style.height = `${Math.min(composer.scrollHeight, maxComposerHeight)}px`;
    composer.style.overflowY = composer.scrollHeight > maxComposerHeight ? "auto" : "hidden";
  }, [input]);

  const handleSignIn = async () => {
    try {
      await signIn();
    } catch (err) {
      console.error(err);
    }
  };

  const handleSignOut = async () => {
    try {
      await signOut();
    } catch (err) {
      console.error(err);
    }
  };

  const submitMessage = async () => {
    if ((!input.trim() && pendingAttachments.length === 0) || loading || uploading) return;

    const baseInput = input.trim() || "Please analyze the attached files.";
    const attachmentReferences = pendingAttachments.length
      ? `\n\nAttached files:\n${pendingAttachments.map((attachment) => `- ${attachment.remotePath}`).join("\n")}`
      : "";
    const composedInput = `${baseInput}${attachmentReferences}`;

    const userMessage: ChatMessage = { role: "user", content: composedInput };
    const isNewSessionTurn = !sessionId;
    const nextMessages = [...messages, userMessage];
    setMessages(nextMessages);
    setInput("");
    setLoading(true);
    setStreamingText("");
    setActivities([]);

    try {
      const accessToken = await getAccessToken();

      const notYetUploaded = pendingAttachments.filter((attachment) => !attachment.uploaded);
      if (sessionId && notYetUploaded.length > 0) {
        setUploading(true);
        await uploadAttachments(accessToken, sessionId, notYetUploaded);
        setPendingAttachments((prev) => prev.map((attachment) => ({ ...attachment, uploaded: true })));
      }

      let nextStreamingText = "";
      const { text, sessionId: nextSessionId } = await streamMessage(
        accessToken,
        nextMessages,
        sessionId,
        (delta) => {
          nextStreamingText += delta;
          setStreamingText(nextStreamingText);
        },
        (activity) => {
          setActivities((prev) => {
            // Deduplicate: skip if same label already exists (except "Thinking…" which collapses to last only)
            if (activity.kind === "reasoning") {
              if (prev.length > 0 && prev[prev.length - 1].kind === "reasoning") {
                return prev;
              }
              return [...prev, activity];
            }
            if (prev.some((a) => a.label === activity.label && a.kind === activity.kind)) {
              return prev;
            }
            return [...prev, activity];
          });
        }
      );

      setSessionId(nextSessionId);
      const responseText = isNewSessionTurn ? withSessionBanner(text) : text;
      setMessages([...nextMessages, { role: "assistant", content: responseText }]);

      if (!sessionId && nextSessionId && notYetUploaded.length > 0) {
        setUploading(true);
        await uploadAttachments(accessToken, nextSessionId, notYetUploaded);
        setUploadMessage(`Uploaded ${notYetUploaded.length} file(s). They are ready for your next prompt.`);
      } else if (notYetUploaded.length > 0) {
        setUploadMessage(`Attached ${notYetUploaded.length} file(s) to this session.`);
      } else {
        setUploadMessage(null);
      }

      setPendingAttachments([]);
      setStreamingText(null);
      bottomRef.current?.scrollIntoView({ behavior: "smooth" });
    } catch (err) {
      const errorState = buildErrorState(err, sessionId);
      const errorContent = [errorState.summary, errorState.details].filter(Boolean).join("\n");
      setMessages((prev) => [...prev, { role: "error", content: errorContent }]);
      setStreamingText(null);
    } finally {
      setUploading(false);
      setLoading(false);
    }
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    void submitMessage();
  };

  const handleComposerKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key !== "Enter" || e.shiftKey) {
      return;
    }

    e.preventDefault();
    void submitMessage();
  };

  const uploadAttachments = async (
    accessToken: string,
    activeSessionId: string,
    attachments: PendingAttachment[]
  ): Promise<void> => {
    for (const attachment of attachments) {
      await uploadSessionFile(accessToken, activeSessionId, attachment.remotePath, attachment.file);
    }
  };

  const handleAddAttachments = async (event: React.ChangeEvent<HTMLInputElement>) => {
    const chosenFiles = event.target.files;
    if (!chosenFiles || chosenFiles.length === 0) {
      return;
    }

    const nextAttachments: PendingAttachment[] = Array.from(chosenFiles).map((file) => {
      const safeName = file.name.replace(/[^a-zA-Z0-9._-]/g, "_");
      const timestamp = Date.now();
      return {
        id: `${file.name}-${file.size}-${file.lastModified}-${timestamp}`,
        file,
        remotePath: `uploads/${timestamp}-${safeName}`,
        mention: `@${file.name}`,
        uploaded: false,
      };
    });

    const dedupedAttachments = nextAttachments.filter((candidate) => {
      return !pendingAttachments.some(
        (existing) =>
          existing.file.name === candidate.file.name &&
          existing.file.size === candidate.file.size &&
          existing.file.lastModified === candidate.file.lastModified
      );
    });

    if (dedupedAttachments.length === 0) {
      event.target.value = "";
      return;
    }

    setPendingAttachments((prev) => [...prev, ...dedupedAttachments]);
    setInput((prev) => {
      let nextInput = prev.trimEnd();
      for (const attachment of dedupedAttachments) {
        if (!nextInput.includes(attachment.mention)) {
          nextInput = `${nextInput}${nextInput ? " " : ""}${attachment.mention}`;
        }
      }
      return nextInput;
    });

    setUploadMessage(null);

    if (!sessionId) {
      setUploadMessage("File(s) queued. They will upload after the first response creates a session.");
      event.target.value = "";
      return;
    }

    try {
      const accessToken = await getAccessToken();

      setUploading(true);
      await uploadAttachments(accessToken, sessionId, dedupedAttachments);
      const uploadedIds = new Set(dedupedAttachments.map((attachment) => attachment.id));
      setPendingAttachments((prev) =>
        prev.map((attachment) =>
          uploadedIds.has(attachment.id) ? { ...attachment, uploaded: true } : attachment
        )
      );
      setUploadMessage(`Uploaded ${dedupedAttachments.length} file(s) to this session.`);
    } catch (err) {
      const errorState = buildErrorState(err, sessionId);
      const errorContent = [errorState.summary, errorState.details].filter(Boolean).join("\n");
      setMessages((prev) => [...prev, { role: "error", content: errorContent }]);
    } finally {
      setUploading(false);
      event.target.value = "";
    }
  };

  const removeAttachment = (attachmentId: string) => {
    console.log("Removing attachment", attachmentId);
  };

  if (!isAuthenticated) {
    return (
      <SignInScreen onSignIn={handleSignIn} onTokenSignIn={signInWithToken} />
    );
  }

  return (
    <div className="chat-shell">
      <header className="chat-header">
        <div className="header-title-block">
          <h1>{AGENT_NAME}</h1>
          <p>{AGENT_SUBTITLE} <span className="version-badge">v{APP_VERSION}</span></p>
        </div>
        <div className="header-right">
          <span className="user-name">{userName}</span>
          <button className="sign-out" onClick={handleSignOut}>Sign out</button>
        </div>
      </header>

      <div className="messages">
        {messages.map((m, i) => (
          <div key={i} className={`message ${m.role}`}>
            {m.role === "assistant" ? (
              <div className="bubble markdown">
                <ReactMarkdown remarkPlugins={[remarkGfm]}>{m.content}</ReactMarkdown>
              </div>
            ) : m.role === "error" ? (
              <div className="bubble error-bubble markdown">
                <ReactMarkdown remarkPlugins={[remarkGfm]}>{m.content}</ReactMarkdown>
              </div>
            ) : (
              <span className="bubble">{m.content}</span>
            )}
          </div>
        ))}
        {streamingText !== null && (
          <div className="message assistant">
            <div className="assistant-content">
              {activities.length > 0 && (
                <details className="activity-log" open>
                  <summary className="activity-summary">
                    {activities[activities.length - 1].label}
                  </summary>
                  <ul className="activity-list">
                    {activities.map((a, i) => (
                      <li key={i} className={`activity-item activity-${a.kind}`}>
                        <span className="activity-icon">
                          {a.kind === "tool" ? "🔧" : a.kind === "reasoning" ? "💭" : "🎯"}
                        </span>
                        {a.label}
                      </li>
                    ))}
                  </ul>
                </details>
              )}
              {streamingText ? (
                <div className="bubble markdown streaming">
                  <ReactMarkdown remarkPlugins={[remarkGfm]}>{streamingText}</ReactMarkdown>
                </div>
              ) : (
                <span className="bubble streaming">…</span>
              )}
            </div>
          </div>
        )}

        <div ref={bottomRef} />
      </div>

      <form onSubmit={handleSubmit} className="chat-input-row">
        <input
          ref={fileInputRef}
          type="file"
          multiple
          onChange={handleAddAttachments}
          disabled={loading || uploading}
          className="hidden-file-input"
        />
        {pendingAttachments.length > 0 && (
          <div className="attachment-strip" aria-label="Attached files">
            {pendingAttachments.map((attachment) => (
              <div key={attachment.id} className="attachment-chip">
                <span className="attachment-name">{attachment.file.name}</span>
                <span className={`attachment-state ${attachment.uploaded ? "uploaded" : "queued"}`}>
                  {attachment.uploaded ? "linked" : sessionId ? "pending" : "queued"}
                </span>
                <button
                  type="button"
                  className="remove-attachment"
                  onClick={() => removeAttachment(attachment.id)}
                  aria-label={`Remove ${attachment.file.name}`}
                >
                  x
                </button>
              </div>
            ))}
          </div>
        )}
        <div className="composer-row">
          <button
            type="button"
            className="attach-button"
            onClick={() => fileInputRef.current?.click()}
            disabled={loading || uploading}
            title="Attach files"
            aria-label="Attach files"
          >
            +
          </button>
          <textarea
            ref={composerRef}
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={handleComposerKeyDown}
            placeholder={`Message ${AGENT_NAME}...`}
            disabled={loading || uploading}
            autoFocus
            className="composer-input"
            rows={1}
          />
          <button type="submit" className="send-button" disabled={loading || uploading || (!input.trim() && pendingAttachments.length === 0)}>
            {loading || uploading ? "…" : "Send"}
          </button>
        </div>
      </form>
      {uploadMessage && <div className="upload-status">{uploadMessage}</div>}
    </div>
  );
}

function SignInScreen({ onSignIn, onTokenSignIn }: { onSignIn: () => void; onTokenSignIn?: (token: string) => void }) {
  const [mode, setMode] = useState<"choose" | "token">("choose");
  const [token, setToken] = useState("");
  const [copied, setCopied] = useState(false);

  const handleCopyCommand = async () => {
    await navigator.clipboard.writeText(AZ_TOKEN_COMMAND);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  const handleTokenSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (token.trim() && onTokenSignIn) {
      onTokenSignIn(token.trim());
    }
  };

  if (mode === "token") {
    return (
      <div className="auth-container">
        <h1>{AGENT_NAME}</h1>
        <p>{AGENT_SUBTITLE}</p>
        <div className="token-auth-panel">
          <p className="token-instructions">Run this command to get a Foundry access token:</p>
          <div className="token-command-block">
            <code className="token-command">{AZ_TOKEN_COMMAND}</code>
            <button type="button" className="copy-button" onClick={handleCopyCommand}>
              {copied ? "✓ Copied" : "Copy"}
            </button>
          </div>
          <form onSubmit={handleTokenSubmit} className="token-form">
            <textarea
              className="token-input"
              placeholder="Paste your access token here..."
              value={token}
              onChange={(e) => setToken(e.target.value)}
              rows={3}
            />
            <button type="submit" disabled={!token.trim()} className="token-connect-button">
              Connect with Token
            </button>
          </form>
          <button className="back-link" onClick={() => setMode("choose")}>
            ← Back to sign-in options
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="auth-container">
      <h1>{AGENT_NAME}</h1>
      <p>{AGENT_SUBTITLE}</p>
      <div className="auth-options">
        <button onClick={onSignIn} className="auth-option-button">
          Sign in with Microsoft
        </button>
        {onTokenSignIn && (
          <button onClick={() => setMode("token")} className="auth-option-button token-option">
            Use Access Token
          </button>
        )}
      </div>
    </div>
  );
}

export default App;
