// Provider credentials minted for authorized session members. The API keys
// stay here; clients only receive scoped, short-lived results.

import type { Config } from './config';

export class NotConfigured extends Error {}

export type VoiceGrantPayload = {
  provider: 'elevenlabs';
  agentId: string;
  /** WebSocket URL for one conversation; valid ~15 minutes. */
  signedUrl: string;
  /** WebRTC conversation token, when the provider returned one. */
  conversationToken?: string;
};

export async function mintVoiceSession(cfg: Config['voice']): Promise<VoiceGrantPayload> {
  const { elevenLabsApiKey: key, elevenLabsAgentId: agentId } = cfg;
  if (!key || !agentId) throw new NotConfigured('voice provider is not configured on the gateway');
  const headers = { 'xi-api-key': key };
  const q = `agent_id=${encodeURIComponent(agentId)}`;

  const res = await fetch(`https://api.elevenlabs.io/v1/convai/conversation/get-signed-url?${q}`, {
    headers,
  });
  if (!res.ok) throw new Error(`ElevenLabs signed URL failed: ${res.status} ${await res.text()}`);
  const { signed_url } = (await res.json()) as { signed_url: string };

  // The WebRTC token is optional; older agents or plans may not offer it.
  let conversationToken: string | undefined;
  try {
    const tok = await fetch(`https://api.elevenlabs.io/v1/convai/conversation/token?${q}`, { headers });
    if (tok.ok) conversationToken = ((await tok.json()) as { token?: string }).token;
  } catch {
    // ignore: signed URL is sufficient
  }
  return { provider: 'elevenlabs', agentId, signedUrl: signed_url, conversationToken };
}

export type IceGrantPayload = {
  iceServers: { urls: string | string[]; username?: string; credential?: string }[];
  /** True when a TURN relay is included (needed across restrictive networks). */
  relay: boolean;
};

export async function mintIceServers(cfg: Config['ice']): Promise<IceGrantPayload> {
  if (cfg.cloudflareTurnKeyId && cfg.cloudflareTurnApiToken) {
    const res = await fetch(
      `https://rtc.live.cloudflare.com/v1/turn/keys/${cfg.cloudflareTurnKeyId}/credentials/generate-ice-servers`,
      {
        method: 'POST',
        headers: {
          authorization: `Bearer ${cfg.cloudflareTurnApiToken}`,
          'content-type': 'application/json',
        },
        body: JSON.stringify({ ttl: cfg.ttlSeconds }),
      }
    );
    if (!res.ok) throw new Error(`Cloudflare TURN failed: ${res.status} ${await res.text()}`);
    const body = (await res.json()) as { iceServers: IceGrantPayload['iceServers'] };
    return { iceServers: body.iceServers, relay: true };
  }
  const iceServers: IceGrantPayload['iceServers'] = [{ urls: cfg.stunUrls }];
  if (cfg.turnUrls?.length && cfg.turnUsername && cfg.turnCredential) {
    iceServers.push({ urls: cfg.turnUrls, username: cfg.turnUsername, credential: cfg.turnCredential });
    return { iceServers, relay: true };
  }
  return { iceServers, relay: false };
}
