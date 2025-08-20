<template>
  <div class="card">
    <h3>Snapshot</h3>
    <div class="row">
      <button @click="refresh" :disabled="loading">刷新</button>
      <span v-if="loading">加载中…</span>
      <span v-else>CPU: {{ snap?.cpu?.usage_percent?.toFixed(1) ?? '-' }}% | 内存: {{ snap?.memory?.used ?? '-' }}/{{ snap?.memory?.total ?? '-' }} MB</span>
      <span v-if="error" style="color:#c00; font-size:12px; margin-left:8px;">{{ error }}</span>
    </div>
  </div>
</template>
<script setup lang="ts">
import { ref, onMounted } from 'vue';
import type { SnapshotResult } from '../api/dto';
import { service } from '../api/service';

const snap = ref<SnapshotResult | null>(null);
const loading = ref(false);
const error = ref<string | null>(null);
let reqSeq = 0;
const withTimeout = async <T>(p: Promise<T>, ms = 6000): Promise<T> => {
  let timer: any; const t = new Promise<never>((_, rej) => { timer = setTimeout(() => rej(new Error('snapshot timeout')), ms); });
  try { return await Promise.race([p, t]) as T; } finally { if (timer) clearTimeout(timer); }
};
const refresh = () => {
  const my = ++reqSeq; error.value = null;
  loading.value = true; const lt = setTimeout(() => { if (my === reqSeq) loading.value = false; }, 800);
  withTimeout(service.snapshot())
    .then(r => { if (my === reqSeq) snap.value = r; })
    .catch((e:any) => { if (my === reqSeq) error.value = e?.message || String(e); })
    .finally(() => { clearTimeout(lt); if (my === reqSeq) loading.value = false; });
};
onMounted(refresh);
</script>
<style scoped>
.card { border: 1px solid #ddd; border-radius: 8px; padding: 12px; }
.row { display: flex; gap: 12px; align-items: center; }
button { padding: 6px 12px; }
</style>
