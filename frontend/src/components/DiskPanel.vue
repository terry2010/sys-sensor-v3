<template>
  <div class="card">
    <h3>Disk</h3>
    <div v-if="!disk">暂无数据</div>
    <template v-else>
      <div class="grid2">
        <div>
          <h4>Totals (I/O)</h4>
          <div class="kv">
            <div><label>Read</label><span>{{ fmtBps(disk.totals?.read_bytes_per_sec ?? disk.read_bytes_per_sec) }}</span></div>
            <div><label>Write</label><span>{{ fmtBps(disk.totals?.write_bytes_per_sec ?? disk.write_bytes_per_sec) }}</span></div>
            <div><label>Read IOPS</label><span>{{ fmtNum(disk.totals?.read_iops) }}</span></div>
            <div><label>Write IOPS</label><span>{{ fmtNum(disk.totals?.write_iops) }}</span></div>
            <div><label>Busy</label><span>{{ fmtPct(disk.totals?.busy_percent) }}</span></div>
            <div><label>Queue</label><span>{{ fmtNum(disk.totals?.queue_length ?? disk.queue_length) }}</span></div>
            <div><label>Avg Read Lat</label><span>{{ fmtMs(disk.totals?.avg_read_latency_ms) }}</span></div>
            <div><label>Avg Write Lat</label><span>{{ fmtMs(disk.totals?.avg_write_latency_ms) }}</span></div>
            <div><label>Source</label><span>{{ disk.totals_source ?? 'unknown' }}</span></div>
          </div>
        </div>
        <div>
          <h4>Capacity</h4>
          <div class="kv">
            <div><label>Total</label><span>{{ fmtBytes(disk.capacity_totals?.total_bytes) }}</span></div>
            <div><label>Used</label><span>{{ fmtBytes(disk.capacity_totals?.used_bytes) }}</span></div>
            <div><label>Free</label><span>{{ fmtBytes(disk.capacity_totals?.free_bytes) }}</span></div>
            <div><label>VM Swapfiles</label><span>{{ fmtBytes(disk.vm_swapfiles_bytes) }}</span></div>
          </div>
        </div>
      </div>
      <details open>
        <summary>Per Physical Disk I/O</summary>
        <table class="tbl">
          <thead>
            <tr>
              <th>Device</th><th>Read</th><th>Write</th><th>R IOPS</th><th>W IOPS</th><th>Busy</th><th>Queue</th><th>R Lat</th><th>W Lat</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(d, i) in perPhysicalSorted" :key="i">
              <td>{{ d.disk_id ?? '#' + i }}</td>
              <td>{{ fmtBps(d.read_bytes_per_sec) }}</td>
              <td>{{ fmtBps(d.write_bytes_per_sec) }}</td>
              <td>{{ fmtNum(d.read_iops) }}</td>
              <td>{{ fmtNum(d.write_iops) }}</td>
              <td>{{ fmtPct(d.busy_percent) }}</td>
              <td>{{ fmtNum(d.queue_length) }}</td>
              <td>{{ fmtMs(d.avg_read_latency_ms) }}</td>
              <td>{{ fmtMs(d.avg_write_latency_ms) }}</td>
            </tr>
          </tbody>
        </table>
      </details>
      <details open>
        <summary>Health</summary>
        <table class="tbl">
          <thead>
            <tr>
              <th>Disk</th><th>Temp(C)</th><th>Health</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="h in (disk.smart_health ?? [])" :key="h.disk_id">
              <td>{{ h.disk_id }}</td>
              <td>{{ h.temperature_c ?? '-' }}</td>
              <td>{{ h.overall_health ?? '-' }}</td>
            </tr>
          </tbody>
        </table>
      </details>
      <details open>
        <summary>Per Physical (Info)</summary>
        <table class="tbl">
          <thead>
            <tr>
              <th>ID</th><th>Model</th><th>Serial</th><th>FW</th><th>Total</th><th>Media</th><th>Interface</th><th>Bus</th><th>RPM</th><th>TRIM</th><th>Removable</th><th>Eject</th><th>LinkSpeed</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="pi in (disk.per_physical_disk ?? [])" :key="pi.id">
              <td>{{ pi.id }}</td>
              <td>{{ pi.model ?? '-' }}</td>
              <td>{{ pi.serial ?? '-' }}</td>
              <td>{{ pi.firmware ?? '-' }}</td>
              <td>{{ fmtBytes(pi.size_total_bytes) }}</td>
              <td>{{ pi.media_type ?? '-' }}</td>
              <td>{{ pi.interface_type ?? '-' }}</td>
              <td>{{ pi.bus_type ?? '-' }}</td>
              <td>{{ fmtNum(pi.spindle_speed_rpm) }}</td>
              <td>{{ pi.trim_supported === true ? 'Yes' : pi.trim_supported === false ? 'No' : '-' }}</td>
              <td>{{ pi.is_removable === true ? 'Yes' : pi.is_removable === false ? 'No' : '-' }}</td>
              <td>{{ pi.eject_capable === true ? 'Yes' : pi.eject_capable === false ? 'No' : '-' }}</td>
              <td>{{ pi.negotiated_link_speed ?? '-' }}</td>
            </tr>
          </tbody>
        </table>
      </details>
      <details open>
        <summary>Per Volume I/O</summary>
        <table class="tbl">
          <thead>
            <tr>
              <th>Volume</th><th>Read</th><th>Write</th><th>R IOPS</th><th>W IOPS</th><th>Busy</th><th>Queue</th><th>R Lat</th><th>W Lat</th><th>Free%</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="v in perVolumeSorted" :key="v.volume_id">
              <td>{{ v.volume_id }}</td>
              <td>{{ fmtBps(v.read_bytes_per_sec) }}</td>
              <td>{{ fmtBps(v.write_bytes_per_sec) }}</td>
              <td>{{ fmtNum(v.read_iops) }}</td>
              <td>{{ fmtNum(v.write_iops) }}</td>
              <td>{{ fmtPct(v.busy_percent) }}</td>
              <td>{{ fmtNum(v.queue_length) }}</td>
              <td>{{ fmtMs(v.avg_read_latency_ms) }}</td>
              <td>{{ fmtMs(v.avg_write_latency_ms) }}</td>
              <td>{{ fmtPct(v.free_percent) }}</td>
            </tr>
          </tbody>
        </table>
      </details>
      <details>
        <summary>Per Volume (Capacity)</summary>
        <table class="tbl">
          <thead>
            <tr>
              <th>Mount</th><th>FS</th><th>Total</th><th>Used</th><th>Free</th><th>Free%</th><th>RO</th><th>Removable</th><th>BitLocker</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="vi in perVolumeInfoSorted" :key="vi.id">
              <td>{{ vi.mount_point }}</td>
              <td>{{ vi.fs_type ?? '-' }}</td>
              <td>{{ fmtBytes(vi.size_total_bytes) }}</td>
              <td>{{ fmtBytes(vi.size_used_bytes) }}</td>
              <td>{{ fmtBytes(vi.size_free_bytes) }}</td>
              <td>{{ fmtPct(vi.free_percent) }}</td>
              <td>{{ vi.read_only === true ? 'Yes' : vi.read_only === false ? 'No' : '-' }}</td>
              <td>{{ vi.is_removable === true ? 'Yes' : vi.is_removable === false ? 'No' : '-' }}</td>
              <td>{{ vi.bitlocker_encryption ?? '-' }}</td>
            </tr>
          </tbody>
        </table>
      </details>
    </template>
  </div>
</template>
<script setup lang="ts">
import { computed } from 'vue';
import { useMetricsStore } from '../stores/metrics';

const metrics = useMetricsStore();
const disk = computed(() => metrics.latest?.disk);

const perPhysicalSorted = computed(() => {
  const arr = (disk.value?.per_physical_disk_io ?? []) as any[];
  return [...arr].sort((a, b) => (String(a.disk_id||'')).localeCompare(String(b.disk_id||'')));
});
const perVolumeSorted = computed(() => {
  const arr = (disk.value?.per_volume_io ?? []) as any[];
  return [...arr].sort((a, b) => String(a.volume_id).localeCompare(String(b.volume_id)));
});
const perVolumeInfoSorted = computed(() => {
  const arr = (disk.value?.per_volume ?? []) as any[];
  return [...arr].sort((a, b) => String(a.mount_point).localeCompare(String(b.mount_point)));
});

const fmtNum = (v: any) => (v==null||!isFinite(v)) ? '-' : String(Math.round(v*10)/10);
const fmtMs = (v: any) => (v==null||!isFinite(v)) ? '-' : `${(v as number).toFixed(2)} ms`;
const fmtPct = (v: any) => (v==null||!isFinite(v)) ? '-' : `${(v as number).toFixed(1)}%`;
const fmtBps = (v: any) => fmtBytes(v) + '/s';
const fmtBytes = (v: any) => {
  if (v==null || !isFinite(v)) return '-';
  const n = Number(v);
  if (n < 1024) return `${n} B`;
  const units = ['KB','MB','GB','TB'];
  let val = n; let i= -1;
  do { val /= 1024; i++; } while (val >= 1024 && i < units.length-1);
  return `${val.toFixed(2)} ${units[i]}`;
};
</script>
<style scoped>
.grid2 { display: grid; grid-template-columns: 1fr; gap: 12px; }
@media (min-width: 900px) { .grid2 { grid-template-columns: 1fr 1fr; } }
.kv { display: grid; grid-template-columns: 120px 1fr; gap: 6px 12px; font-size: 12px; }
.kv > div { display: contents; }
.kv label { color: #666; }
.kv span { font-weight: 600; }
.tbl { width: 100%; border-collapse: collapse; font-size: 12px; }
.tbl th, .tbl td { border-bottom: 1px solid #eee; padding: 6px; text-align: left; }
.tbl th { background: #fafafa; }
</style>
