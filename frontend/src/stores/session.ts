import { defineStore } from 'pinia';
import type { HelloResult } from '../api/dto';
import { service } from '../api/service';

export const useSessionStore = defineStore('session', {
  state: () => ({ session: null as HelloResult | null, loading: false, error: '' as string | '' }),
  actions: {
    async init() {
      this.loading = true; this.error = '';
      const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
      let lastErr: any = null;
      for (let i = 0; i < 5; i++) {
        try {
          this.session = await service.hello();
          this.error = '';
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
      }
      this.loading = false;
    }
  }
});
