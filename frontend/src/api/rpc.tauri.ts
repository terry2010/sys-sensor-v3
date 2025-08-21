// Tauri v2 适配：通过 @tauri-apps/api 使用 invoke 与事件系统
import type {
  QueryHistoryParams,
  QueryHistoryResult,
  SnapshotResult,
  HelloResult,
  SetConfigParams,
  StartParams,
  StopParams,
  BurstSubscribeParams,
} from './dto';

async function rpcCall<T>(method: string, params?: any, timeoutMs = 6000): Promise<T> {
  const { invoke } = await import('@tauri-apps/api/core');
  // 约定在 Rust 端实现 invoke("rpc_call", { method, params })
  const call = invoke('rpc_call', { method, params }) as Promise<T>;
  if (!timeoutMs || timeoutMs <= 0) return call;
  const timeout = new Promise<T>((_, reject) => {
    const id = setTimeout(() => {
      clearTimeout(id);
      reject(new Error(`RPC timeout: ${method}`));
    }, timeoutMs);
  });
  return Promise.race([call, timeout]) as Promise<T>;
}

export const tauriRpc = {
  async hello(): Promise<HelloResult> { return rpcCall<HelloResult>('hello', { app_version: 'fe-mock', protocol_version: 1, token: 'dev', capabilities: [] }); },
  async snapshot(): Promise<SnapshotResult> { return rpcCall<SnapshotResult>('snapshot', {}); },
  async query_history(p: QueryHistoryParams): Promise<QueryHistoryResult> { return rpcCall<QueryHistoryResult>('query_history', p); },
  async set_config(p: SetConfigParams) { return rpcCall<any>('set_config', p); },
  async start(p?: StartParams) { return rpcCall<any>('start', p ?? {}); },
  async stop(p: StopParams = {}) { return rpcCall<any>('stop', p); },
  async burst_subscribe(p: BurstSubscribeParams) { return rpcCall<any>('burst_subscribe', p); },
  async subscribe_metrics(enable: boolean = true) { return rpcCall<any>('subscribe_metrics', { enable }); },
  async bridge_subscribe(enable: boolean = true) {
    const { invoke } = await import('@tauri-apps/api/core');
    return invoke('bridge_set_subscribe', { enable });
  },
  onMetrics(listener: (payload: any) => void) {
    let unlisten: (() => void) | null = null;
    // 动态引入事件 API，避免在纯 Web 环境编译/运行报错
    import('@tauri-apps/api/event')
      .then(({ listen }) => listen('metrics', (evt: any) => {
        // 标记已收到 metrics，停止 ensureEventBridge 的重试/强制订阅
        try { const w: any = typeof window !== 'undefined' ? window : {}; w.__METRICS_READY = true; } catch {}
        listener(evt?.payload);
      }))
      .then((fn) => { unlisten = fn; })
      .catch(() => { /* 非 Tauri 环境或事件桥未就绪，忽略 */ });
    return () => { if (unlisten) unlisten(); };
  }
};

export async function ensureEventBridge() {
  // 非 Tauri 环境会在 import 阶段抛错，这里静默忽略
  const startOnce = async () => {
    try {
      const { invoke } = await import('@tauri-apps/api/core');
      await invoke('start_event_bridge');
      // 标记宿主为 Tauri（首次成功后）
      const w: any = typeof window !== 'undefined' ? window : {};
      if (!w.__IS_TAURI__) w.__IS_TAURI__ = true;
      // 补强：桥接建立后，再次强制开启订阅，避免首个 subscribe 早于桥建立而丢失
      try { await invoke('bridge_set_subscribe', { enable: true }); } catch { /* ignore */ }
    }
    catch (e) { /* 忽略重复启动等错误 */ }
  };
  // 立即尝试一次
  await startOnce();
  // 若尚未收到 metrics，则每 3s 重试一次，直到准备就绪
  const w: any = typeof window !== 'undefined' ? window : {};
  const timerKey = '__EVENT_BRIDGE_TIMER__';
  if (!w[timerKey]) {
    w[timerKey] = setInterval(async () => {
      try {
        if (w.__METRICS_READY) { clearInterval(w[timerKey]); w[timerKey] = null; return; }
        await startOnce();
      } catch { /* ignore */ }
    }, 3000);
  }
}
