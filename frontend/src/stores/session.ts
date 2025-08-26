import { defineStore } from 'pinia';
import type { HelloResult } from '../api/dto';
import { service } from '../api/service';
import { useEventsStore } from './events';

export const useSessionStore = defineStore('session', {
  state: () => ({ session: null as HelloResult | null, loading: false, error: '' as string | '' }),
  actions: {
    async init() {
      this.loading = true; this.error = '';
      const events = useEventsStore();
      const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
      const withTimeout = async <T>(p: Promise<T>, ms = 15000): Promise<T> => {
        let timer: any;
        const t = new Promise<never>((_, rej) => { timer = setTimeout(() => rej(new Error('session hello timeout')), ms); });
        try { return await Promise.race([p, t]) as T; }
        finally { if (timer) clearTimeout(timer); }
      };
      let lastErr: any = null;
      for (let i = 0; i < 5; i++) {
        try {
          const r = await withTimeout(service.hello());
          this.session = r;
          this.error = '';
          try { events.push({ ts: Date.now(), type: 'info', payload: { evt: 'hello_ok', server: r?.server_version, proto: r?.protocol_version } }); } catch {}
          break;
        } catch (e: any) {
          lastErr = e;
          // 仅对命名管道打开失败做短暂重试
          const msg = String(e?.message || e || '');
          if (!/open named pipe/i.test(msg)) {
            this.error = msg; break;
          }
          await sleep(400 + i * 200);
        }
      }
      if (!this.session && lastErr) {
        this.error = String(lastErr?.message || lastErr);
        try { events.push({ ts: Date.now(), type: 'error', payload: { evt: 'hello_fail', err: this.error } }); } catch {}
      }
      this.loading = false;
    }
  }
});
