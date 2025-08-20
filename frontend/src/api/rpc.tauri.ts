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

async function rpcCall<T>(method: string, params?: any): Promise<T> {
  const { invoke } = await import('@tauri-apps/api/core');
  // 约定在 Rust 端实现 invoke("rpc_call", { method, params })
  return await invoke('rpc_call', { method, params }) as T;
}

export const tauriRpc = {
  async hello(): Promise<HelloResult> { return rpcCall<HelloResult>('hello', { app_version: 'fe-mock', protocol_version: 1, token: 'dev', capabilities: [] }); },
  async snapshot(): Promise<SnapshotResult> { return rpcCall<SnapshotResult>('snapshot', {}); },
  async query_history(p: QueryHistoryParams): Promise<QueryHistoryResult> { return rpcCall<QueryHistoryResult>('query_history', p); },
  async set_config(p: SetConfigParams) { return rpcCall<any>('set_config', p); },
  async start(p?: StartParams) { return rpcCall<any>('start', p ?? {}); },
  async stop(p: StopParams = {}) { return rpcCall<any>('stop', p); },
  async burst_subscribe(p: BurstSubscribeParams) { return rpcCall<any>('burst_subscribe', p); },
  onMetrics(listener: (payload: any) => void) {
    let unlisten: (() => void) | null = null;
    // 动态引入事件 API，避免在纯 Web 环境编译/运行报错
    import('@tauri-apps/api/event')
      .then(({ listen }) => listen('metrics', (evt: any) => listener(evt?.payload)))
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
