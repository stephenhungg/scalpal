import { StrictMode, useMemo } from 'react';
import { createRoot } from 'react-dom/client';
import { SpacetimeDBProvider } from 'spacetimedb/react';
import App from './App';
import { SPACETIMEDB_DB, SPACETIMEDB_URI, TOKEN_KEY } from './config';
import { LiveDataProvider } from './data/live';
import { load, save } from './lib/storage';
import { DbConnection } from './module_bindings';
import './styles.css';

function Root() {
  const builder = useMemo(
    () =>
      DbConnection.builder()
        .withUri(SPACETIMEDB_URI)
        .withDatabaseName(SPACETIMEDB_DB)
        .withToken(load(TOKEN_KEY) ?? undefined)
        .onConnect((_conn, _identity, token) => save(TOKEN_KEY, token)),
    []
  );
  return (
    <SpacetimeDBProvider connectionBuilder={builder}>
      <LiveDataProvider>
        <App />
      </LiveDataProvider>
    </SpacetimeDBProvider>
  );
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Root />
  </StrictMode>
);
