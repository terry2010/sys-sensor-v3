<template>
  <div class="card">
    <h3>Snapshot</h3>
    <div class="row">
      <button @click="refresh" :disabled="loading">刷新</button>
      <span v-if="loading">加载中…</span>
      <span v-else>CPU: {{ snap?.cpu?.usage_percent?.toFixed(1) ?? '-' }}% | 内存: {{ snap?.memory?.used ?? '-' }}/{{ snap?.memory?.total ?? '-' }} MB</span>
    </div>
  </div>
</template>
<script setup lang="ts">
import { ref, onMounted } from 'vue';
import type { SnapshotResult } from '../api/dto';
import { service } from '../api/service';

const snap = ref<SnapshotResult | null>(null);
const loading = ref(false);
const refresh = async () => { loading.value = true; snap.value = await service.snapshot().finally(()=> loading.value=false); };
onMounted(refresh);
</script>
<style scoped>
.card { border: 1px solid #ddd; border-radius: 8px; padding: 12px; }
.row { display: flex; gap: 12px; align-items: center; }
button { padding: 6px 12px; }
</style>
