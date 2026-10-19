import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { RUNTIME_CONFIG } from './app-config';
import { AgentInfo, ChatRequest, ChatResponse } from './models';

@Injectable({ providedIn: 'root' })
export class ChatService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(RUNTIME_CONFIG).apiBaseUrl;

  send(request: ChatRequest): Promise<ChatResponse> {
    return firstValueFrom(this.http.post<ChatResponse>(`${this.baseUrl}/api/chat`, request));
  }

  reset(conversationId: string): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>(`${this.baseUrl}/api/chat/${encodeURIComponent(conversationId)}`),
    );
  }

  agents(): Promise<{ degraded: boolean; agents: AgentInfo[] }> {
    return firstValueFrom(
      this.http.get<{ degraded: boolean; agents: AgentInfo[] }>(`${this.baseUrl}/api/agents`),
    );
  }
}
