import { afterEach, describe, expect, it, vi } from "vitest";

// Runs the real setup script against a stubbed ElevenLabs API, so the test sees exactly the tool configs it would deploy.
describe("jarvis:setup", () => {
  afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); });

  it("deploys swap_instrument and highlight_instrument as client tools with the open-case instrument enum", async () => {
    const sent: { method: string; path: string; body: any }[] = [];
    vi.stubEnv("ELEVENLABS_API_KEY", "test-key");
    for (const name of ["ELEVENLABS_AGENT_ID", "PATIENT_AGENT_ID", "INTERVIEW_PATIENT_AGENT_ID"]) vi.stubEnv(name, "");
    vi.spyOn(console, "log").mockImplementation(() => {});
    vi.stubGlobal("fetch", async (url: string, init: { method: string; body?: string }) => {
      const path = url.replace("https://api.elevenlabs.io/v1/convai", "");
      sent.push({ method: init.method, path, body: init.body ? JSON.parse(init.body) : undefined });
      const reply = init.method === "GET" ? { tools: [] } : path === "/tools" ? { id: `tool-${sent.length}` } : { agent_id: `agent-${sent.length}` };
      return new Response(JSON.stringify(reply), { status: 200 });
    });
    await import("../scripts/setup-jarvis-agent.js");
    await vi.waitFor(() => expect(sent.filter((x) => x.path === "/agents/create")).toHaveLength(3));

    const tools = sent.filter((x) => x.method === "POST" && x.path === "/tools").map((x) => x.body.tool_config);
    const instruments = ["skin_marker", "scalpel", "toothed_forceps", "retractor", "babcock", "hemostat", "right_angle_clamp",
      "metzenbaum_scissors", "suture_tie", "suction_irrigator", "laparoscope_30"];
    const swap = tools.find((t) => t.name === "swap_instrument");
    // The headset resolves these; a server tool or a missing enum would let the agent name tools the stand does not have.
    expect(swap).toMatchObject({ type: "client", expects_response: true });
    expect(swap.parameters.properties.instrument.enum).toEqual(instruments);
    expect(swap.parameters.properties.hand.enum).toEqual(["left", "right", "either"]);
    expect(swap.parameters.required).toEqual(["instrument"]);
    const highlight = tools.find((t) => t.name === "highlight_instrument");
    expect(highlight).toMatchObject({ type: "client", expects_response: true });
    expect(highlight.parameters.properties.instrument.enum).toEqual(instruments);

    // Only the coach agent gets them, never the patient voices.
    const ids = (name: string) => sent.find((x) => x.path === "/agents/create" && x.body.name === name)!.body.conversation_config.agent.prompt.tool_ids as string[];
    const idOf = (name: string) => `tool-${sent.findIndex((x) => x.method === "POST" && x.path === "/tools" && x.body.tool_config.name === name) + 1}`;
    expect(ids("Scalpal Coach")).toEqual(expect.arrayContaining([idOf("swap_instrument"), idOf("highlight_instrument")]));
    expect(ids("Scalpal Patient")).not.toContain(idOf("swap_instrument"));
  });
});
