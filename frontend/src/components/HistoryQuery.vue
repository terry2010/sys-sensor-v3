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
import { useHistoryQueryStore } from '../stores/historyQuery';

const now = Date.now();
const from = ref<number>(now - 60_000);
const to = ref<number>(0);
const step = ref<number | null>(1000);
const mods = ref<string[]>(['cpu','memory']);

const items = ref<any[]>([]);
const loading = ref(false);
const error = ref<string | null>(null);
const preview = computed(() => JSON.stringify({ from: from.value, to: to.value, step: step.value ?? undefined, modules: mods.value, items: items.value.slice(0, 5) }, null, 2));
const hq = useHistoryQueryStore();
let reqSeq = 0;
const withTimeout = async <T>(p: Promise<T>, ms = 6000): Promise<T> => {
  let timer: any;
  const t = new Promise<never>((_, rej) => { timer = setTimeout(() => rej(new Error('query timeout')), ms); });
  try { return await Promise.race([p, t]) as T; }
  finally { if (timer) clearTimeout(timer); }
};

async function onQuery() {
  const my = ++reqSeq;
  error.value = null; items.value = [];
  // 局部 loading 最多展示 800ms，避免给人“卡住”的感受
  loading.value = true;
  const loadingTimer = setTimeout(() => { if (my === reqSeq) loading.value = false; }, 800);

  const p = withTimeout(service.queryHistory({
    from_ts: from.value,
    to_ts: to.value,
    step_ms: step.value ?? undefined,
    modules: mods.value,
  } as any));

  p.then((r) => {
    const arr = (r as any)?.items ?? [];
    if (my === reqSeq) {
      items.value = Array.isArray(arr) ? arr : [];
      hq.setResult({ from_ts: from.value, to_ts: to.value, step_ms: step.value ?? undefined, modules: mods.value }, items.value);
    }
  }).catch((e: any) => {
    if (my === reqSeq) error.value = e?.message || String(e);
  }).finally(() => {
    clearTimeout(loadingTimer);
    if (my === reqSeq) loading.value = false;
  });
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
