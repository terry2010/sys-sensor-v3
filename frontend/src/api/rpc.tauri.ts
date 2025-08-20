// Tauri 适配层占位：通过 window.__TAURI__ 调用后端（后续生成 src-tauri 后可直接生效）
// 这里不引入任何 Tauri 类型定义，使用 (window as any).__TAURI__ 以避免在 Web 环境编译报错。
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

function tauri() {
  return (window as any).__TAURI__;
}

async function rpcCall<T>(method: string, params?: any): Promise<T> {
  const api = tauri();
  if (!api?.invoke) throw new Error('Tauri 未就绪');
  // 约定在 Rust 端实现 invoke("rpc_call", { method, params })
  return await api.invoke('rpc_call', { method, params });
}

export const tauriRpc = {
  async hello(): Promise<HelloResult> { return rpcCall<HelloResult>('hello', { app_version: 'fe-mock', protocol_version: 1, token: '', capabilities: [] }); },
  async snapshot(): Promise<SnapshotResult> { return rpcCall<SnapshotResult>('snapshot', {}); },
  async query_history(p: QueryHistoryParams): Promise<QueryHistoryResult> { return rpcCall<QueryHistoryResult>('query_history', p); },
  async set_config(p: SetConfigParams) { return rpcCall<any>('set_config', p); },
  async start(p?: StartParams) { return rpcCall<any>('start', p ?? {}); },
  async stop(p: StopParams = {}) { return rpcCall<any>('stop', p); },
  async burst_subscribe(p: BurstSubscribeParams) { return rpcCall<any>('burst_subscribe', p); },
  onMetrics(listener: (payload: any) => void) {
    const api = tauri();
    if (!api?.event?.listen) throw new Error('Tauri 事件系统未就绪');
    let unlisten: (() => void) | null = null;
    api.event.listen('metrics', (evt: any) => listener(evt?.payload)).then((fn: any) => { unlisten = fn; });
    return () => { if (unlisten) unlisten(); };
  }
};

export async function ensureEventBridge() {
  const api = tauri();
  if (!api?.invoke) return;
  try {
    await api.invoke('start_event_bridge');
  } catch (e) {
    // 忽略重复启动等错误
    console.warn('start_event_bridge failed/ignored:', e);
  }
}
