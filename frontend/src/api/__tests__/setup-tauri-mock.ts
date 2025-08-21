import { vi } from 'vitest';

// 通过模块级 mock 转发到 window.__TAURI__，便于在各用例中用 spy/vi.fn 控制行为
vi.mock('@tauri-apps/api/core', () => {
  const g: any = globalThis as any;
  return {
    invoke: (...args: any[]) => {
      const fn = g?.window?.__TAURI__?.invoke;
      if (typeof fn === 'function') return fn(...args);
      // 缺省返回 rejected Promise，便于用例感知未设置 mock
      return Promise.reject(new Error('mock: __TAURI__.invoke not set'));
    },
  };
});

vi.mock('@tauri-apps/api/event', () => {
  const g: any = globalThis as any;
  return {
    listen: (event: string, handler: (e: any) => void) => {
      const fn = g?.window?.__TAURI__?.event?.listen;
      if (typeof fn === 'function') return fn(event, handler);
      // 缺省返回一个空的 unlisten
      return Promise.resolve(() => {});
    },
  };
});
