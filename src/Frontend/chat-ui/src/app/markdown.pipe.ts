import { Pipe, PipeTransform } from '@angular/core';
import { marked } from 'marked';

/**
 * Renders agent replies (Markdown tables, lists, code) to HTML. The result is bound with [innerHTML],
 * which Angular sanitizes, so scripts and event handlers in model output are stripped.
 */
@Pipe({ name: 'markdown' })
export class MarkdownPipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    return value ? (marked.parse(value, { async: false, gfm: true, breaks: true }) as string) : '';
  }
}
