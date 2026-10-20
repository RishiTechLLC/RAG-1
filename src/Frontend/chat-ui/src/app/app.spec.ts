import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { App } from './app';
import { RUNTIME_CONFIG } from './app-config';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RUNTIME_CONFIG, useValue: { apiBaseUrl: '' } },
      ],
    }).compileComponents();
  });

  it('shows suggestions before the first message', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('h1')?.textContent).toContain('Campus Assistant');
    expect(compiled.querySelectorAll('.suggestions button').length).toBeGreaterThan(0);
  });

  it('posts the message to /api/chat and renders the reply with its trace', async () => {
    const fixture = TestBed.createComponent(App);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    http.expectOne('/api/agents').flush({ degraded: false, agents: [] });

    const sending = (
      fixture.componentInstance as unknown as { send(t: string): Promise<void> }
    ).send('hello');
    const req = http.expectOne('/api/chat');
    expect(req.request.body).toEqual({ message: 'hello', conversationId: null, agent: 'auto' });
    req.flush({
      conversationId: 'c1',
      reply: '**Hi!**',
      agent: 'orchestrator',
      steps: [
        {
          caller: 'orchestrator',
          target: 'knowledge_agent',
          kind: 'agent',
          input: '{}',
          durationMs: 5,
          succeeded: true,
        },
      ],
      durationMs: 10,
    });
    await sending;
    await fixture.whenStable();

    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.message.assistant strong')?.textContent).toBe('Hi!');
    expect(compiled.querySelector('.trace summary')?.textContent).toContain('1 step');
    http.verify();
  });
});
