import { defineStore } from 'pinia';
import type { HelloResult } from '../api/dto';
import { service } from '../api/service';

export const useSessionStore = defineStore('session', {
  state: () => ({ session: null as HelloResult | null, loading: false, error: '' as string | '' }),
  actions: {
    async init() {
      this.loading = true; this.error = '';
      try { this.session = await service.hello(); }
      catch (e: any) { this.error = String(e?.message || e); }
      finally { this.loading = false; }
    }
  }
});
