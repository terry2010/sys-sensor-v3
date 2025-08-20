// 占位版 RPC：当前为 Web Mock，方便在无 Tauri 情况下开发 UI。
// 后续将替换为：Tauri backend 命名管道 JSON-RPC 客户端，通过 invoke/event 与此处对接。

import { mockRpc } from './rpc.mock';
import { tauriRpc } from './rpc.tauri';

// 在浏览器（Vite）环境下没有 __TAURI__，在 Tauri 宿主中会注入该对象
const isTauri = typeof window !== 'undefined' && !!(window as any).__TAURI__;

export const rpc = isTauri ? tauriRpc : mockRpc;

