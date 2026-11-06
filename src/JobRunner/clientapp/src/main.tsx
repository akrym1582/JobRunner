import React from "react";
import ReactDOM from "react-dom/client";
import { SWRConfig } from "swr";
import { App } from "./App";
import "./style.css";
ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <SWRConfig
      value={{
        revalidateOnFocus: true,
        errorRetryInterval: 5000,
        errorRetryCount: 3,
      }}
    >
      <App />
    </SWRConfig>
  </React.StrictMode>,
);
