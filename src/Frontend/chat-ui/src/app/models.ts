export type AgentChoice = 'auto' | 'knowledge' | 'campus' | 'writer';

export interface TraceStep {
  caller: string;
  target: string;
  kind: 'agent' | 'tool' | 'mcp-tool' | string;
  input: string;
  durationMs: number;
  succeeded: boolean;
}

export interface ChatRequest {
  message: string;
  conversationId?: string | null;
  agent?: AgentChoice;
}

export interface ChatResponse {
  conversationId: string;
  reply: string;
  agent: string;
  steps: TraceStep[];
  durationMs: number;
  inputTokens?: number | null;
  outputTokens?: number | null;
}

export interface ChatMessage {
  id: number;
  role: 'user' | 'assistant' | 'error';
  content: string;
  agent?: string;
  steps?: TraceStep[];
  durationMs?: number;
}

export interface AgentInfo {
  name: string;
  description: string;
  tools: string[];
}
