import { defineStore } from 'pinia';

export type BridgeState = {
  status: 'idle' | 'connecting' | 'connected' | 'disconnected' | 'error';
  rx: number;
  err: number;
  lastEvent: string;
  lastAt: number;
};

export const useBridgeStore = defineStore('bridge', {
  state: (): BridgeState => ({ status: 'idle', rx: 0, err: 0, lastEvent: '', lastAt: 0 }),
  actions: {
    init() {
      const w: any = typeof window !== 'undefined' ? window : {};
      // 初始状态读取
      try { if (w.__BRIDGE_STATUS__) this.status = w.__BRIDGE_STATUS__; } catch {}
      // 监听 tauri 事件（在 web 环境下会安全失败）
      (async () => {
        try {
          const { listen } = await import('@tauri-apps/api/event');
          await listen('bridge_handshake', () => { this.status = 'connected'; this.lastEvent = 'handshake'; this.lastAt = Date.now(); });
          await listen('bridge_disconnected', () => { this.status = 'disconnected'; this.lastEvent = 'disconnected'; this.lastAt = Date.now(); });
          await listen('bridge_error', () => { this.status = 'error'; this.err++; this.lastEvent = 'error'; this.lastAt = Date.now(); });
          await listen('bridge_rx', () => { this.rx++; this.lastEvent = 'rx'; this.lastAt = Date.now(); });
        } catch { /* 非 Tauri 环境 */ }
      })();
    }
  }
});
