import { createContext, useContext, useState, useCallback, type ReactNode } from "react";
import { useIsAuthenticated, useMsal } from "@azure/msal-react";
import { loginRequest } from "./authConfig";
import { config } from "./config";

export interface AuthState {
  isAuthenticated: boolean;
  userName: string;
  getAccessToken: () => Promise<string>;
  signIn: () => Promise<void>;
  signOut: () => Promise<void>;
  signInWithToken?: (token: string) => void;
}

const AuthContext = createContext<AuthState | null>(null);

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) {
    throw new Error("useAuth must be used within an AuthProvider");
  }
  return ctx;
}

function StaticTokenProvider({ token, children }: { token: string; children: ReactNode }) {
  const auth: AuthState = {
    isAuthenticated: true,
    userName: "Token User",
    getAccessToken: async () => token,
    signIn: async () => {},
    signOut: async () => { window.location.reload(); },
  };

  return <AuthContext.Provider value={auth}>{children}</AuthContext.Provider>;
}

function MsalAuthProvider({ children }: { children: ReactNode }) {
  const isAuthenticated = useIsAuthenticated();
  const { instance, accounts } = useMsal();
  const [runtimeToken, setRuntimeToken] = useState<string | null>(null);

  const signInWithToken = useCallback((token: string) => {
    setRuntimeToken(token);
  }, []);

  if (runtimeToken) {
    return (
      <StaticTokenProvider token={runtimeToken}>
        {children}
      </StaticTokenProvider>
    );
  }

  const auth: AuthState = {
    isAuthenticated,
    userName: accounts[0]?.name ?? accounts[0]?.username ?? "User",
    getAccessToken: async () => {
      const result = await instance
        .acquireTokenSilent({ ...loginRequest, account: accounts[0] })
        .catch(() => instance.acquireTokenPopup({ ...loginRequest, account: accounts[0] }));
      return result.accessToken;
    },
    signIn: async () => {
      const result = await instance.loginPopup(loginRequest);
      instance.setActiveAccount(result.account);
    },
    signOut: async () => {
      const account = instance.getActiveAccount() ?? accounts[0];
      await instance.logoutPopup({
        account,
        postLogoutRedirectUri: loginRequest.redirectUri,
        mainWindowRedirectUri: window.location.origin,
      });
      instance.setActiveAccount(null);
    },
    signInWithToken,
  };

  return <AuthContext.Provider value={auth}>{children}</AuthContext.Provider>;
}

export function AuthProvider({ children }: { children: ReactNode }) {
  if (config.foundryAccessToken) {
    return <StaticTokenProvider token={config.foundryAccessToken}>{children}</StaticTokenProvider>;
  }
  return <MsalAuthProvider>{children}</MsalAuthProvider>;
}
