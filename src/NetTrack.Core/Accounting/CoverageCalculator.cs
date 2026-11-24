using NetTrack.Core.Abstractions;

namespace NetTrack.Core.Accounting;

public readonly record struct CoverageSegment(long HourStartUtcMs, long StartUtcMs, long EndUtcMs);

/// <summary>
/// 有效覆盖区间的跨小时拆分、相邻合并与并集毫秒计算工具。
/// </summary>
public static class CoverageCalculator
{
    /// <summary>
    /// 将连续有效区间 [startUtcMs, endUtcMs] 按北京时间自然小时边界拆分为一个或多个片段。
    /// </summary>
    public static List<CoverageSegment> SplitAcrossBeijingHours(long startUtcMs, long endUtcMs)
    {
        var result = new List<CoverageSegment>();
        if (endUtcMs <= startUtcMs)
        {
            return result;
        }

        var cursor = startUtcMs;
        while (cursor < endUtcMs)
        {
            var hourStart = BeijingTime.GetHourStartUtcMs(cursor);
            var hourEnd = hourStart + BeijingTime.HourMs;
            var segEnd = Math.Min(endUtcMs, hourEnd);
            result.Add(new CoverageSegment(hourStart, cursor, segEnd));
            cursor = segEnd;
        }

        return result;
    }

    /// <summary>
    /// 将同一小时内相邻或重叠的片段合并，减少写入 SQLite 的行数。
    /// </summary>
    public static List<CoverageSegment> MergeSegments(IEnumerable<CoverageSegment> segments)
    {
        var grouped = segments
            .Where(s => s.EndUtcMs > s.StartUtcMs)
            .GroupBy(s => s.HourStartUtcMs);

        var merged = new List<CoverageSegment>();
        foreach (var group in grouped)
        {
            var sorted = group.OrderBy(s => s.StartUtcMs).ThenBy(s => s.EndUtcMs).ToList();
            var curStart = sorted[0].StartUtcMs;
            var curEnd = sorted[0].EndUtcMs;

            for (var i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].StartUtcMs <= curEnd)
                {
                    if (sorted[i].EndUtcMs > curEnd)
                    {
                        curEnd = sorted[i].EndUtcMs;
                    }
                }
                else
                {
                    merged.Add(new CoverageSegment(group.Key, curStart, curEnd));
                    curStart = sorted[i].StartUtcMs;
                    curEnd = sorted[i].EndUtcMs;
                }
            }

            merged.Add(new CoverageSegment(group.Key, curStart, curEnd));
        }

        return merged;
    }

    /// <summary>
    /// 计算指定窗口 [windowStartUtcMs, windowEndUtcMs] 内已覆盖片段的并集区间列表及总毫秒数。
    /// </summary>
    public static (List<(long Start, long End)> UnionIntervals, long TotalMs) ComputeUnion(
        IEnumerable<(long Start, long End)> rawSegments,
        long windowStartUtcMs,
        long windowEndUtcMs)
    {
        var union = new List<(long Start, long End)>();
        if (windowEndUtcMs <= windowStartUtcMs)
        {
            return (union, 0L);
        }

        var clipped = new List<(long Start, long End)>();
        foreach (var (s, e) in rawSegments)
        {
            var cs = Math.Max(s, windowStartUtcMs);
            var ce = Math.Min(e, windowEndUtcMs);
            if (ce > cs)
            {
                clipped.Add((cs, ce));
            }
        }

        if (clipped.Count == 0)
        {
            return (union, 0L);
        }

        clipped.Sort((a, b) =>
        {
            var cmp = a.Start.CompareTo(b.Start);
            return cmp != 0 ? cmp : a.End.CompareTo(b.End);
        });

        var curStart = clipped[0].Start;
        var curEnd = clipped[0].End;
        long totalMs = 0;

        for (var i = 1; i < clipped.Count; i++)
        {
            if (clipped[i].Start <= curEnd)
            {
                if (clipped[i].End > curEnd)
                {
                    curEnd = clipped[i].End;
                }
            }
            else
            {
                union.Add((curStart, curEnd));
                totalMs += curEnd - curStart;
                curStart = clipped[i].Start;
                curEnd = clipped[i].End;
            }
        }

        union.Add((curStart, curEnd));
        totalMs += curEnd - curStart;
        return (union, totalMs);
    }

    /// <summary>
    /// 计算多个目标各自已排序不相交并集区间的交集及总毫秒数（用于多节点观察合计覆盖：只有所有节点都被观察到的时段才算覆盖）。
    /// </summary>
    public static (List<(long Start, long End)> IntersectionIntervals, long TotalMs) ComputeIntersection(
        IReadOnlyList<List<(long Start, long End)>> perTargetUnions)
    {
        if (perTargetUnions.Count == 0)
        {
            return (new List<(long, long)>(), 0L);
        }

        var current = perTargetUnions[0];
        if (current.Count == 0)
        {
            return (new List<(long, long)>(), 0L);
        }

        for (var k = 1; k < perTargetUnions.Count; k++)
        {
            var next = perTargetUnions[k];
            if (next.Count == 0)
            {
                return (new List<(long, long)>(), 0L);
            }

            var intersected = new List<(long Start, long End)>();
            int i = 0, j = 0;
            while (i < current.Count && j < next.Count)
            {
                var start = Math.Max(current[i].Start, next[j].Start);
                var end = Math.Min(current[i].End, next[j].End);
                if (end > start)
                {
                    intersected.Add((start, end));
                }

                if (current[i].End < next[j].End)
                {
                    i++;
                }
                else
                {
                    j++;
                }
            }

            current = intersected;
            if (current.Count == 0)
            {
                return (current, 0L);
            }
        }

        long totalMs = 0;
        foreach (var (s, e) in current)
        {
            totalMs += e - s;
        }
        return (current, totalMs);
    }

    /// <summary>
    /// 计算待更新区间 [pendingStart, pendingEnd] 扣除已覆盖并集后的净毫秒数。
    /// </summary>
    public static long ComputePendingWithoutOverlapMs(
        long pendingStartUtcMs,
        long pendingEndUtcMs,
        IReadOnlyList<(long Start, long End)> coveredUnion)
    {
        if (pendingEndUtcMs <= pendingStartUtcMs)
        {
            return 0L;
        }

        var totalPending = pendingEndUtcMs - pendingStartUtcMs;
        long overlap = 0;
        foreach (var (s, e) in coveredUnion)
        {
            var os = Math.Max(s, pendingStartUtcMs);
            var oe = Math.Min(e, pendingEndUtcMs);
            if (oe > os)
            {
                overlap += oe - os;
            }
        }

        return Math.Max(0L, totalPending - overlap);
    }

    /// <summary>
    /// 使用最大余数法将毫秒分量换算为和等于 elapsedSeconds 的整数秒展示值。
    /// </summary>
    public static (int CoveredSec, int GapSec, int PendingSec) RoundMsToDisplaySeconds(
        long coveredMs,
        long gapMs,
        long pendingMs,
        long elapsedMs)
    {
        var elapsedSec = (int)Math.Round(elapsedMs / 1000.0, MidpointRounding.AwayFromZero);
        if (elapsedSec <= 0)
        {
            return (0, 0, 0);
        }

        var values = new[] { coveredMs / 1000.0, gapMs / 1000.0, pendingMs / 1000.0 };
        var floors = new int[3];
        var remainders = new double[3];
        var sum = 0;

        for (var i = 0; i < 3; i++)
        {
            floors[i] = (int)Math.Floor(values[i]);
            remainders[i] = values[i] - floors[i];
            sum += floors[i];
        }

        var diff = elapsedSec - sum;
        while (diff > 0)
        {
            var bestIdx = 0;
            for (var i = 1; i < 3; i++)
            {
                if (remainders[i] > remainders[bestIdx])
                {
                    bestIdx = i;
                }
            }
            floors[bestIdx]++;
            remainders[bestIdx] = -1.0;
            diff--;
        }

        while (diff < 0)
        {
            var bestIdx = -1;
            for (var i = 0; i < 3; i++)
            {
                if (floors[i] > 0 && (bestIdx < 0 || remainders[i] < remainders[bestIdx]))
                {
                    bestIdx = i;
                }
            }
            if (bestIdx < 0)
            {
                break;
            }
            floors[bestIdx]--;
            remainders[bestIdx] = 2.0;
            diff++;
        }

        return (floors[0], floors[1], floors[2]);
    }
}
