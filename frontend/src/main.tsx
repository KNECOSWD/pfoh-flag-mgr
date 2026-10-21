import React from "react";
import ReactDOM from "react-dom/client";
import {
  PublicClientApplication,
  EventType,
  AuthenticationResult
} from "@azure/msal-browser";
import { MsalProvider } from "@azure/msal-react";
import { msalConfig } from "./authConfig";
import App from "./App";
import { publishSignInError, readAuthErrorFromLocation } from "./signInError";
import { isRecoverableAuthError } from "./tokenRequest.js";
import "./style.css";

const msalInstance = new PublicClientApplication(msalConfig);

msalInstance.addEventCallback((event) => {
  if (event.eventType === EventType.LOGIN_SUCCESS && event.payload) {
    const payload = event.payload as AuthenticationResult;
    if (payload.account) {
      msalInstance.setActiveAccount(payload.account);
    }
  }

  if (event.eventType === EventType.LOGIN_FAILURE) {
    publishSignInError(event.error);
  }

  if (event.eventType === EventType.ACQUIRE_TOKEN_FAILURE && !isRecoverableAuthError(event.error)) {
    publishSignInError(event.error);
  }

  if (event.eventType === EventType.HANDLE_REDIRECT_END) {
    const leftover = readAuthErrorFromLocation();
    if (leftover) {
      publishSignInError(leftover);
    }
  }
});

async function startApp() {
  try {
    await msalInstance.initialize();
    await msalInstance.handleRedirectPromise();

    const accounts = msalInstance.getAllAccounts();
    if (accounts.length > 0) {
      msalInstance.setActiveAccount(accounts[0]);
    }
  } catch (error) {
    publishSignInError(error);
  }

  const leftover = readAuthErrorFromLocation();
  if (leftover) {
    publishSignInError(leftover);
  }

  ReactDOM.createRoot(document.getElementById("root")!).render(
    <React.StrictMode>
      <MsalProvider instance={msalInstance}>
        <App />
      </MsalProvider>
    </React.StrictMode>
  );
}

startApp().catch((error) => {
  console.error("Failed to start app", error);
  publishSignInError(error);
});
