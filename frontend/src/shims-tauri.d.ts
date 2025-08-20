declare module '@tauri-apps/api/core' {
  export const invoke: <T = any>(cmd: string, args?: Record<string, any>) => Promise<T>;
}

declare module '@tauri-apps/api/event' {
  export function listen<T = any>(event: string, handler: (event: { payload: T }) => void): Promise<() => void>;
}

// 全局窗口标志（由前端运行时设置）
declare global {
  interface Window {
    __IS_TAURI__?: boolean;
    __METRICS_READY?: boolean;
    __EVENT_BRIDGE_TIMER__?: any;
  }
}

export {};
