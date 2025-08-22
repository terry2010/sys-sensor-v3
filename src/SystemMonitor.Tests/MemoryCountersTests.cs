using System;
using System.Linq;
using System.Reflection;
using SystemMonitor.Service.Services;
using SystemMonitor.Service.Services.Samplers;
using Xunit;

namespace SystemMonitor.Tests
{
    public class MemoryCountersTests
    {
        [Fact]
        public void GetMemoryInfoMb_NoThrow_And_ReasonableRanges()
        {
            var (total, used) = SystemInfo.GetMemoryInfoMb();
            Assert.True(total >= 0);
            Assert.True(used >= 0);
            Assert.True(used <= total);
        }

        [Fact]
        public void GetMemoryDetail_NoThrow_And_BoundedFields()
        {
            var d = SystemInfo.GetMemoryDetail();
            if (d.TotalMb.HasValue && d.UsedMb.HasValue && d.TotalMb.Value > 0)
            {
                Assert.InRange(d.UsedMb!.Value, 0, d.TotalMb!.Value);
                if (d.PercentUsed.HasValue)
                {
                    Assert.InRange(d.PercentUsed!.Value, 0.0, 100.0);
                }
            }
            // Optional fields: if present, they should be non-negative or within bounds
            long?[] nonNegativeLongs = new long?[]
            {
                d.AvailableMb, d.CachedMb, d.CommitLimitMb, d.CommitUsedMb,
                d.SwapTotalMb, d.SwapUsedMb, d.CompressedBytesMb, d.PoolPagedMb,
                d.PoolNonpagedMb, d.StandbyCacheMb, d.WorkingSetTotalMb
            };
            foreach (var v in nonNegativeLongs)
            {
                if (v.HasValue) Assert.True(v.Value >= 0);
            }
            if (d.CommitPercent.HasValue)
            {
                Assert.InRange(d.CommitPercent!.Value, 0.0, 100.0);
            }
            if (d.MemoryPressurePercent.HasValue)
            {
                Assert.InRange(d.MemoryPressurePercent!.Value, 0.0, 100.0);
                Assert.Contains(d.MemoryPressureLevel, new[] { null, "green", "yellow", "red" });
            }
        }

        [Fact]
        public void MemoryCounters_Read_NoThrow_And_CacheWithin200ms()
        {
            var m1 = MemoryCounters.Instance.Read();
            var m2 = MemoryCounters.Instance.Read(); // within <200ms typical test run
            // Ensure tuple equality for cached read (value-based comparison)
            Assert.Equal(m1, m2);

            // Validate bounds if values exist
            double?[] nonNegative = new double?[]
            {
                m1.CacheMb, m1.CommitLimitMb, m1.CommitUsedMb, m1.CommitPercent,
                m1.SwapTotalMb, m1.SwapUsedMb, m1.PageReadsPerSec, m1.PageWritesPerSec,
                m1.PageFaultsPerSec, m1.CompressedMb, m1.PoolPagedMb, m1.PoolNonpagedMb,
                m1.StandbyCacheMb, m1.WorkingSetTotalMb
            };
            foreach (var v in nonNegative)
            {
                if (v.HasValue)
                {
                    Assert.True(v.Value >= 0);
                }
            }
            if (m1.CommitPercent.HasValue)
            {
                Assert.InRange(m1.CommitPercent!.Value, 0.0, 100.0);
            }
        }

        [Fact]
        public void MemoryCounters_ForceFallback_NoInitObjects_NoThrow()
        {
            // Using reflection to simulate environment with no PerformanceCounter init
            var t = typeof(MemoryCounters);
            var inst = MemoryCounters.Instance;

            // reset init to true without creating counters; subsequent SafeNextValue should handle nulls
            var initTriedField = t.GetField("_initTried", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(initTriedField);
            initTriedField!.SetValue(inst, true);

            // Null out counter fields if any
            string[] fields = new[]
            {
                "_cacheBytes","_commitLimit","_committedBytes","_pageReadsPerSec","_pageWritesPerSec",
                "_pageFaultsPerSec","_compressedBytes","_poolPagedBytes","_poolNonpagedBytes"
            };
            foreach (var f in fields)
            {
                var fi = t.GetField(f, BindingFlags.NonPublic | BindingFlags.Instance);
                if (fi != null) fi.SetValue(inst, null);
            }
            var standbyList = t.GetField("_standbyCounters", BindingFlags.NonPublic | BindingFlags.Instance);
            standbyList?.SetValue(inst, new System.Collections.Generic.List<PerformanceCounter>());

            var r = inst.Read();
            // Should not throw and return tuple with nulls or non-negative numbers
            double?[] vals = new double?[]
            {
                r.CacheMb, r.CommitLimitMb, r.CommitUsedMb, r.CommitPercent,
                r.SwapTotalMb, r.SwapUsedMb, r.PageReadsPerSec, r.PageWritesPerSec,
                r.PageFaultsPerSec, r.CompressedMb, r.PoolPagedMb, r.PoolNonpagedMb,
                r.StandbyCacheMb, r.WorkingSetTotalMb
            };
            foreach (var v in vals)
            {
                if (v.HasValue) Assert.True(v.Value >= 0);
            }
            if (r.CommitPercent.HasValue)
            {
                Assert.InRange(r.CommitPercent!.Value, 0.0, 100.0);
            }
        }
    }
}
