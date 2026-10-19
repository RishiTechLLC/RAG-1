import { Component, ElementRef, inject, OnInit, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { ChatService } from './chat.service';
import { MarkdownPipe } from './markdown.pipe';
import { AgentChoice, AgentInfo, ChatMessage } from './models';

@Component({
  selector: 'app-root',
  imports: [FormsModule, MarkdownPipe],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnInit {
  private readonly chat = inject(ChatService);
  private readonly scroller = viewChild<ElementRef<HTMLElement>>('scroller');
  private nextId = 1;

  protected readonly messages = signal<ChatMessage[]>([]);
  protected readonly busy = signal(false);
  protected readonly agentInfo = signal<AgentInfo[]>([]);
  protected readonly degraded = signal(false);
  protected readonly conversationId = signal<string | null>(null);
  protected draft = '';
  protected agent: AgentChoice = 'auto';

  protected readonly agentOptions: { value: AgentChoice; label: string }[] = [
    { value: 'auto', label: 'Auto (orchestrator)' },
    { value: 'knowledge', label: 'Knowledge base' },
    { value: 'campus', label: 'Campus data (MCP)' },
    { value: 'writer', label: 'Writer' },
  ];

  protected readonly suggestions = [
    'What is the late submission policy for assignments?',
    'Show the enrollments and grades for student S2026005',
    'Which majors have the highest average GPA?',
    'When do final exams start this term, and what is the weather in Seattle?',
    'Draft an email to CS340 students about the refund policy if they drop after week 3',
  ];

  async ngOnInit(): Promise<void> {
    try {
      const result = await this.chat.agents();
      this.agentInfo.set(result.agents);
      this.degraded.set(result.degraded);
    } catch {
      // The chat still works; the agent panel just stays empty.
    }
  }

  protected async send(text?: string): Promise<void> {
    const message = (text ?? this.draft).trim();
    if (!message || this.busy()) {
      return;
    }

    this.draft = '';
    this.push({ role: 'user', content: message });
    this.busy.set(true);

    try {
      const response = await this.chat.send({
        message,
        conversationId: this.conversationId(),
        agent: this.agent,
      });
      this.conversationId.set(response.conversationId);
      this.push({
        role: 'assistant',
        content: response.reply,
        agent: response.agent,
        steps: response.steps,
        durationMs: response.durationMs,
      });
    } catch (err) {
      this.push({ role: 'error', content: describeError(err) });
    } finally {
      this.busy.set(false);
    }
  }

  protected onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      void this.send();
    }
  }

  protected async newChat(): Promise<void> {
    const id = this.conversationId();
    this.messages.set([]);
    this.conversationId.set(null);
    if (id) {
      await this.chat.reset(id).catch(() => undefined);
    }
  }

  protected label(name: string | undefined): string {
    return (name ?? '').replace(/_/g, ' ');
  }

  private push(message: Omit<ChatMessage, 'id'>): void {
    this.messages.update((list) => [...list, { ...message, id: this.nextId++ }]);
    queueMicrotask(() => {
      const el = this.scroller()?.nativeElement;
      if (el) {
        el.scrollTop = el.scrollHeight;
      }
    });
  }
}

function describeError(err: unknown): string {
  if (err instanceof HttpErrorResponse) {
    if (err.status === 0) {
      return 'Could not reach the Agents API. Is it running?';
    }
    const detail =
      (err.error as { detail?: string; title?: string } | null)?.detail ?? err.error?.title;
    return `The assistant failed (${err.status}). ${detail ?? err.message}`;
  }
  return 'Something went wrong. Please try again.';
}
