<template>
  <div class="card">
    <h3>History Query</h3>
    <div class="row">
      <label>From(ms): <input type="number" v-model.number="from" /></label>
      <label>To(ms 0=now): <input type="number" v-model.number="to" /></label>
      <label>Step(ms, optional): <input type="number" v-model.number="step" /></label>
    </div>
    <div class="row">
      <label><input type="checkbox" value="cpu" v-model="mods" /> cpu</label>
      <label><input type="checkbox" value="memory" v-model="mods" /> memory</label>
      <button @click="onQuery">queryHistory()</button>
    </div>
    <div class="row info">
      <span>items: {{ items.length }}</span>
      <span v-if="loading">loading...</span>
      <span v-if="error" class="err">{{ error }}</span>
    </div>
    <div class="log">
      <textarea :value="preview" readonly></textarea>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import { service } from '../api/service';

const now = Date.now();
const from = ref<number>(now - 60_000);
const to = ref<number>(0);
const step = ref<number | null>(1000);
const mods = ref<string[]>(['cpu','memory']);

const items = ref<any[]>([]);
const loading = ref(false);
const error = ref<string | null>(null);
const preview = computed(() => JSON.stringify({ from: from.value, to: to.value, step: step.value ?? undefined, modules: mods.value, items: items.value.slice(0, 5) }, null, 2));

async function onQuery() {
  loading.value = true; error.value = null; items.value = [];
  try {
    const r = await service.queryHistory({
      from_ts: from.value,
      to_ts: to.value,
      step_ms: step.value ?? undefined,
      modules: mods.value,
    } as any);
    const arr = (r as any)?.items ?? [];
    items.value = Array.isArray(arr) ? arr : [];
  } catch (e: any) {
    error.value = e?.message || String(e);
  } finally {
    loading.value = false;
  }
}
</script>

<style scoped>
.card { padding: 12px; border: 1px solid #eee; border-radius: 8px; }
.row { display: flex; gap: 8px; margin: 6px 0; flex-wrap: wrap; align-items: center; }
.row label { display: inline-flex; align-items: center; gap: 4px; }
button { padding: 6px 10px; }
.info { font-size: 12px; color: #444; gap: 12px; }
.err { color: #c00; }
.log textarea { width: 100%; min-height: 160px; font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
</style>
