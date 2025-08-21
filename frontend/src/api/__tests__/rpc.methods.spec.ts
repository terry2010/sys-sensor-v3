import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest';
import { tauriRpc } from '../rpc.tauri';

const g = globalThis as any;

describe('tauriRpc invoke calls', () => {
  const invokeMock = vi.fn();
  const listenMock = vi.fn();

  beforeEach(() => {
    g.window = {};
    g.window.__TAURI__ = { invoke: invokeMock, event: { listen: listenMock } };
    invokeMock.mockReset();
    listenMock.mockReset();
  });

  afterEach(() => {
    delete g.window;
  });

  it('set_config sends proper payload', async () => {
    invokeMock.mockResolvedValueOnce({ ok: true });
    await tauriRpc.set_config({ base_interval_ms: 1000, persist: true });
    expect(invokeMock).toHaveBeenCalledWith('rpc_call', {
      method: 'set_config',
      params: { base_interval_ms: 1000, persist: true }
    });
  });

  it('start sends proper payload', async () => {
    invokeMock.mockResolvedValueOnce({ ok: true });
    await tauriRpc.start({ modules: ['cpu'] });
    expect(invokeMock).toHaveBeenCalledWith('rpc_call', {
      method: 'start',
      params: { modules: ['cpu'] }
    });
  });

  it('stop sends proper payload', async () => {
    invokeMock.mockResolvedValueOnce({ ok: true });
    await tauriRpc.stop();
    expect(invokeMock).toHaveBeenCalledWith('rpc_call', {
      method: 'stop',
      params: undefined
    });
  });

  it('burst_subscribe sends proper payload', async () => {
    invokeMock.mockResolvedValueOnce({ ok: true });
    await tauriRpc.burst_subscribe({ modules: ['cpu'], interval_ms: 500, ttl_ms: 3000 });
    expect(invokeMock).toHaveBeenCalledWith('rpc_call', {
      method: 'burst_subscribe',
      params: { modules: ['cpu'], interval_ms: 500, ttl_ms: 3000 }
    });
  });
});
