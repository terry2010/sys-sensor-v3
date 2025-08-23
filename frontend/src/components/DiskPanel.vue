<template>
  <div class="card">
    <h3>Disk</h3>
    <div v-if="!disk">暂无数据</div>
    <div v-else class="hint" v-if="smartHint">
      {{ smartHint }}
    </div>
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
      <details>
        <summary>SATA SMART (Common Attributes)</summary>
        <div v-if="sataHealthRows.length===0" class="subhint">未检测到 SATA/ATA 设备或无可展示的通用属性。</div>
        <table class="tbl" v-else>
          <thead>
            <tr>
              <th>Disk</th>
              <th>Temp(C)</th>
              <th>POH</th>
              <th>Realloc</th>
              <th>Pending</th>
              <th>CRC</th>
              <th>Overall</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="h in sataHealthRows" :key="'sata-'+h.disk_id">
              <td>{{ h.disk_id }}</td>
              <td :class="clsTemp(h.temperature_c)">{{ fmtNum(h.temperature_c) }}</td>
              <td>{{ fmtNum(h.power_on_hours) }}</td>
              <td :class="nz(h.reallocated_sector_count)">{{ fmtNum(h.reallocated_sector_count) }}</td>
              <td :class="nz(h.pending_sector_count)">{{ fmtNum(h.pending_sector_count) }}</td>
              <td :class="nz(h.udma_crc_error_count)">{{ fmtNum(h.udma_crc_error_count) }}</td>
              <td>{{ h.overall_health ?? '-' }}</td>
            </tr>
          </tbody>
        </table>
      </details>
      <details>
        <summary>Latency Quality</summary>
        <div class="subhint">当前仅展示平均延迟，p50/p95/p99 预留占位（后端提供后自动填充）。</div>
        <table class="tbl">
          <thead>
            <tr>
              <th>Disk</th>
              <th>Read Avg</th>
              <th>Write Avg</th>
              <th>p50</th>
              <th>p95</th>
              <th>p99</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="d in perPhysicalSorted" :key="'lat-'+d.disk_id">
              <td>{{ d.disk_id }}</td>
              <td>{{ fmtMs(d.avg_read_latency_ms) }}</td>
              <td>{{ fmtMs(d.avg_write_latency_ms) }}</td>
              <td>-</td>
              <td>-</td>
              <td>-</td>
            </tr>
          </tbody>
        </table>
      </details>
      <details>
        <summary>Reliability & Events</summary>
        <table class="tbl">
          <thead>
            <tr>
              <th>Disk</th>
              <th>Unsafe Shutdowns</th>
              <th>Thermal Throttle</th>
              <th>NVMe Media Errors</th>
              <th>UDMA CRC Errors</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="h in (disk.smart_health ?? [])" :key="'rel-'+h.disk_id">
              <td>{{ h.disk_id }}</td>
              <td :class="nz(h.unsafe_shutdowns)">{{ fmtNum(h.unsafe_shutdowns) }}</td>
              <td :class="nz(h.thermal_throttle_events)">{{ fmtNum(h.thermal_throttle_events) }}</td>
              <td :class="nz(h.nvme_media_errors)">{{ fmtNum(h.nvme_media_errors) }}</td>
              <td :class="nz(h.udma_crc_error_count)">{{ fmtNum(h.udma_crc_error_count) }}</td>
            </tr>
          </tbody>
        </table>
      </details>
      <details>
        <summary>NVMe Details</summary>
        <div v-if="nvmeHealthRows.length===0" class="subhint">未获取到 NVMe 详情，可能因：驱动不支持、USB 桥接、或无管理员权限。</div>
        <table class="tbl">
          <thead>
            <tr>
              <th>Disk</th>
              <th>Avail Spare</th>
              <th>Spare Thresh</th>
              <th>Spare Below?</th>
              <th>Media Errors</th>
              <th>Power Cycles</th>
              <th>POH</th>
              <th>CritWarn</th>
              <th>Temp1</th>
              <th>Temp2</th>
              <th>Temp3</th>
              <th>Temp4</th>
              <th>NS Count</th>
              <th>LBA Size</th>
              <th>NSZE</th>
              <th>NUSE</th>
              <th>Capacity</th>
              <th>In-Use</th>
              <th>EUI64</th>
              <th>NGUID</th>
              <th>Warn Flags</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="h in nvmeHealthRows" :key="h.disk_id">
              <td>{{ h.disk_id }}</td>
              <td :class="clsSpare(h.nvme_available_spare, h.nvme_spare_threshold)">{{ fmtPct(h.nvme_available_spare) }}</td>
              <td>{{ fmtPct(h.nvme_spare_threshold) }}</td>
              <td>{{ spareBelow(h) ? 'Yes' : (spareBelow(h)===false ? 'No' : '-') }}</td>
              <td>{{ fmtNum(h.nvme_media_errors) }}</td>
              <td>{{ fmtNum(h.nvme_power_cycles) }}</td>
              <td>{{ fmtNum(h.nvme_power_on_hours) }}</td>
              <td>{{ fmtHexByte(h.nvme_critical_warning) }}</td>
              <td>{{ fmtNum(h.nvme_temp_sensor1_c) }}</td>
              <td>{{ fmtNum(h.nvme_temp_sensor2_c) }}</td>
              <td>{{ fmtNum(h.nvme_temp_sensor3_c) }}</td>
              <td>{{ fmtNum(h.nvme_temp_sensor4_c) }}</td>
              <td>{{ fmtNum(h.nvme_namespace_count) }}</td>
              <td>{{ fmtBytes(h.nvme_namespace_lba_size_bytes) }}</td>
              <td>{{ fmtNum(h.nvme_namespace_size_lba) }}</td>
              <td>{{ fmtNum(h.nvme_namespace_inuse_lba) }}</td>
              <td>{{ fmtBytes(h.nvme_namespace_capacity_bytes) }}</td>
              <td>{{ fmtBytes(h.nvme_namespace_inuse_bytes) }}</td>
              <td>{{ h.nvme_eui64 ?? '-' }}</td>
              <td>{{ h.nvme_nguid ?? '-' }}</td>
              <td>
                <span v-for="(w, wi) in parseCritWarn(h.nvme_critical_warning)" :key="wi" class="tag" :class="w.cls">{{ w.text }}</span>
              </td>
            </tr>
          </tbody>
        </table>
      </details>
      <details open>
        <summary>Health</summary>
        <table class="tbl">
          <thead>
            <tr>
              <th>Disk</th>
              <th>Temp(C)</th>
              <th>Health</th>
              <th>PowerOn(h)</th>
              <th>Realloc</th>
              <th>Pending</th>
              <th>CRC</th>
              <th>NVMe Used%</th>
              <th>Data Read</th>
              <th>Data Written</th>
              <th>Ctrl Busy(min)</th>
              <th>Unsafe Shutdowns</th>
              <th>Throttle Events</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="h in (disk.smart_health ?? [])" :key="h.disk_id">
              <td>{{ h.disk_id }}</td>
              <td :class="clsTemp(h.temperature_c)">{{ fmtNum(h.temperature_c) }}</td>
              <td>{{ h.overall_health ?? '-' }}</td>
              <td>{{ fmtNum(h.power_on_hours) }}</td>
              <td>{{ fmtNum(h.reallocated_sector_count) }}</td>
              <td>{{ fmtNum(h.pending_sector_count) }}</td>
              <td>{{ fmtNum(h.udma_crc_error_count) }}</td>
              <td :class="clsUsed(h.nvme_percentage_used)">{{ fmtPct(h.nvme_percentage_used) }}</td>
              <td>{{ fmtHealthDataUnits(h.nvme_data_units_read) }}</td>
              <td>{{ fmtHealthDataUnits(h.nvme_data_units_written) }}</td>
              <td>{{ fmtNum(h.nvme_controller_busy_time_min) }}</td>
              <td>{{ fmtNum(h.unsafe_shutdowns) }}</td>
              <td>{{ fmtNum(h.thermal_throttle_events) }}</td>
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

// 顶部 SMART 提示：当 smart_health 为空或所有项几乎为空时给出原因提示
const smartHint = computed(() => {
  const d: any = disk.value;
  if (!d) return '';
  const sh = d.smart_health as any[] | undefined;
  const isEmpty = !sh || sh.length === 0;
  // 判断是否 USB 盘存在（常见不透传 SMART）
  const hasUsb = (d.per_physical_disk ?? []).some((pi: any) => String(pi.interface_type||'').toLowerCase()==='usb');
  if (isEmpty) {
    if (hasUsb) return '未能获取 SMART：USB 桥接可能不支持直通 SMART。';
    return '未能获取 SMART：可能需要管理员权限或驱动不支持。';
  }
  return '';
});

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

// 仅显示具有 NVMe 相关数据的行（任一 NVMe 字段非空）
const nvmeHealthRows = computed(() => {
  const arr = (disk.value?.smart_health ?? []) as any[];
  const hasNvme = (h: any) => (
    h?.nvme_available_spare != null || h?.nvme_spare_threshold != null || h?.nvme_media_errors != null ||
    h?.nvme_power_cycles != null || h?.nvme_power_on_hours != null || h?.nvme_critical_warning != null ||
    h?.nvme_temp_sensor1_c != null || h?.nvme_temp_sensor2_c != null || h?.nvme_temp_sensor3_c != null || h?.nvme_temp_sensor4_c != null ||
    h?.nvme_namespace_count != null || h?.nvme_namespace_lba_size_bytes != null || h?.nvme_namespace_capacity_bytes != null ||
    h?.nvme_eui64 != null || h?.nvme_nguid != null
  );
  return arr.filter(hasNvme);
});

// disk_id -> interface_type 映射
const ifaceByDiskId = computed(() => {
  const map = new Map<string, string>();
  const info = (disk.value?.per_physical_disk ?? []) as any[];
  for (const it of info) map.set(String(it.disk_id), String(it.interface_type||''));
  return map;
});
// SATA 行：根据接口或是否具备 SATA 常见字段来选择
const sataHealthRows = computed(() => {
  const arr = (disk.value?.smart_health ?? []) as any[];
  return arr.filter(h => {
    const iface = (ifaceByDiskId.value.get(String(h.disk_id))||'').toLowerCase();
    const looksSata = iface.includes('sata') || iface.includes('ata');
    const hasSataFields = (h.reallocated_sector_count!=null) || (h.pending_sector_count!=null) || (h.udma_crc_error_count!=null);
    return looksSata || hasSataFields;
  });
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

// 非零高亮
const nz = (v: any) => {
  const n = Number(v);
  if (!isFinite(n) || n<=0) return '';
  if (n > 0) return 'warn';
  return '';
};
const fmtHexByte = (v: any) => (v==null || !isFinite(v)) ? '-' : '0x' + Number(v).toString(16).toUpperCase().padStart(2, '0');
// Health Data Units: LHM 多数以 GB/GiB 数值给出；若数值像“字节级”超大，则用字节格式，否则按 GB 显示
const fmtHealthDataUnits = (v: any) => {
  if (v==null || !isFinite(v)) return '-';
  const n = Number(v);
  // 经验阈值：超过 2^31 近似判为字节
  if (n >= 2147483648) return fmtBytes(n);
  // 以 GB 显示，两位小数
  const gb = Math.round(n * 100) / 100;
  return `${gb.toFixed(2)} GB`;
};

// 颜色分类：温度、已用百分比、可用备用
const clsTemp = (v: any) => {
  const n = Number(v);
  if (!isFinite(n)) return '';
  if (n >= 80) return 'crit';
  if (n >= 70) return 'warn';
  return '';
};
const clsUsed = (v: any) => {
  const n = Number(v);
  if (!isFinite(n)) return '';
  if (n >= 95) return 'crit';
  if (n >= 80) return 'warn';
  return '';
};
const clsSpare = (avail: any, thr: any) => {
  const a = Number(avail), t = Number(thr);
  if (!isFinite(a) || !isFinite(t)) return '';
  if (a <= t) return 'crit';
  if (a <= t + 5) return 'warn';
  return '';
};
const spareBelow = (h: any): boolean | null => {
  const a = Number(h?.nvme_available_spare), t = Number(h?.nvme_spare_threshold);
  if (!isFinite(a) || !isFinite(t)) return null;
  return a <= t;
};

// 解析 Critical Warning 比特
// Bit0: Available Spare Below Threshold
// Bit1: Temperature Threshold
// Bit2: NVM Subsystem Reliability
// Bit3: Media/Data Integrity Error
// Bit4: Volatile Memory Backup Failed / Read-only (实现以常见文案为准)
const parseCritWarn = (v: any) => {
  const n = Number(v);
  if (!isFinite(n) || n === 0) return [] as { text: string; cls: string }[];
  const flags: { text: string; cls: string }[] = [];
  if (n & 0x01) flags.push({ text: 'Spare<Thresh', cls: 'crit' });
  if (n & 0x02) flags.push({ text: 'Temp', cls: 'warn' });
  if (n & 0x04) flags.push({ text: 'Reliability', cls: 'warn' });
  if (n & 0x08) flags.push({ text: 'MediaErr', cls: 'crit' });
  if (n & 0x10) flags.push({ text: 'RO/VMB', cls: 'warn' });
  return flags;
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
.hint { margin: 6px 0 12px; padding: 8px 10px; background: #fff7e6; border: 1px solid #ffd591; color: #874d00; border-radius: 4px; font-size: 12px; }
.subhint { margin: 6px 0 8px; color: #888; font-size: 12px; }
.warn { color: #d46b08; }
.crit { color: #a8071a; font-weight: 600; }
.tag { display: inline-block; padding: 2px 6px; margin: 2px 4px 2px 0; border-radius: 3px; background: #f5f5f5; color: #555; border: 1px solid #eee; font-size: 11px; }
</style>
