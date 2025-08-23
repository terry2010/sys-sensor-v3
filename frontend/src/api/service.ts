import type { QueryHistoryParams, QueryHistoryResult, SnapshotResult, HelloResult, SnapshotParams, GetConfigResult } from './dto';
import { rpc } from './rpc';

export const service = {
  hello(): Promise<HelloResult> { return rpc.hello(); },
  snapshot(p?: SnapshotParams): Promise<SnapshotResult> { return p ? rpc.snapshot(p) : rpc.snapshot(); },
  queryHistory(p: QueryHistoryParams): Promise<QueryHistoryResult> { return rpc.query_history(p); },
  onMetrics: rpc.onMetrics,
  // new methods
  setConfig(p: import('./dto').SetConfigParams) { return (rpc as any).set_config?.(p); },
  start(p?: import('./dto').StartParams) { return (rpc as any).start?.(p); },
  // stop 为无参方法，严禁传参（否则被识别为 stop/1）
  stop() { return (rpc as any).stop?.(); },
  burstSubscribe(p: import('./dto').BurstSubscribeParams) { return (rpc as any).burst_subscribe?.(p); },
  getConfig(): Promise<GetConfigResult> { return (rpc as any).get_config?.(); },
  async subscribeMetrics(p: boolean | { enable: boolean } = true) {
    const enable = typeof p === 'boolean' ? p : !!p?.enable;
    const impl: any = rpc;
    // 优先走桥内切换，避免与事件桥连接分离导致的订阅错位
    if (impl.bridge_subscribe) {
      // 在桥接调用前后发射调试事件，明确来源为用户
      let emit: ((event: string, payload?: any) => Promise<void>) | null = null;
      try { ({ emit } = await import('@tauri-apps/api/event') as any); } catch { /* 非 Tauri 环境忽略 */ }
      try {
        if (emit) await emit('bridge_subscribe', { enable: enable ?? true, src: 'user' });
      } catch { /* 忽略事件发送错误 */ }
      try {
        const r = await impl.bridge_subscribe(enable ?? true);
        try { if (emit) await emit('bridge_subscribe_ack', { enable: enable ?? true, src: 'user', ok: true }); } catch { }
        return r;
      } catch (e: any) {
        try { if (emit) await emit('bridge_subscribe_ack', { enable: enable ?? true, src: 'user', ok: false, err: e?.message || String(e) }); } catch { }
        throw e;
      }
    }
    return impl.subscribe_metrics?.(enable ?? true);
  },
};
