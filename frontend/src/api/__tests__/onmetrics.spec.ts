import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { tauriRpc } from '../rpc.tauri';

const g = globalThis as any;

describe('onMetrics listener', () => {
  const listenMock = vi.fn();
  const invokeMock = vi.fn();

  beforeEach(() => {
    g.window = {};
    g.window.__TAURI__ = { invoke: invokeMock, event: { listen: listenMock } };
    listenMock.mockReset();
    invokeMock.mockReset();
  });

  afterEach(() => {
    delete g.window;
  });

  it('should register listen on metrics and return unlisten', async () => {
    const un = vi.fn();
    // simulate tauri event.listen resolves to unlisten
    listenMock.mockResolvedValueOnce(un);
    const handler = vi.fn();
    const unlisten = tauriRpc.onMetrics((p) => handler(p));

    // wait microtask
    await Promise.resolve();

    expect(listenMock).toHaveBeenCalledTimes(1);
    expect(listenMock.mock.calls[0][0]).toBe('metrics');

    // call unlisten
    unlisten();
    expect(un).toHaveBeenCalledTimes(1);
  });
});
