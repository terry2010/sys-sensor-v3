// RPC 适配：动态在运行时选择 tauri 或 mock，避免模块初始化时误判
import { mockRpc } from './rpc.mock';
import { tauriRpc } from './rpc.tauri';

export function isTauriHost(): boolean {
  const w: any = typeof window !== 'undefined' ? window : {};
  // 三重探测：显式标记 或 全局 __TAURI__/__TAURI_INTERNALS__（Tauri 环境会注入）
  return !!(w.__IS_TAURI__ || w.__TAURI__ || w.__TAURI_INTERNALS__);
}

export function getRpc() {
  return isTauriHost() ? tauriRpc : mockRpc;
}

// 兼容旧用法：提供一个 Proxy，在属性访问时转发到当前真实实现
export const rpc: any = new Proxy({}, {
  get(_t, p: string | symbol) {
    const impl: any = getRpc();
    return impl[p as any];
  }
});

