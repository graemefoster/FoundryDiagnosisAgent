import React from "react";
import ReactDOM from "react-dom/client";
import { PublicClientApplication } from "@azure/msal-browser";
import { MsalProvider } from "@azure/msal-react";
import { msalConfig } from "./authConfig";
import { AuthProvider } from "./AuthContext";
import { config } from "./config";
import App from "./App";
import "./index.css";

function renderApp(msalWrapper?: (children: React.ReactNode) => React.ReactNode) {
  const wrap = msalWrapper ?? ((c: React.ReactNode) => c);
  ReactDOM.createRoot(document.getElementById("root")!).render(
    <React.StrictMode>
      {wrap(
        <AuthProvider>
          <App />
        </AuthProvider>
      )}
    </React.StrictMode>
  );
}

if (config.foundryAccessToken) {
  // No MSAL needed – render immediately with static token auth
  renderApp();
} else {
  const msalInstance = new PublicClientApplication(msalConfig);
  msalInstance.initialize().then(() => {
    const activeAccount = msalInstance.getActiveAccount();
    const accounts = msalInstance.getAllAccounts();

    if (!activeAccount && accounts.length > 0) {
      msalInstance.setActiveAccount(accounts[0]);
    }

    renderApp((children) => (
      <MsalProvider instance={msalInstance}>{children}</MsalProvider>
    ));
  });
}
