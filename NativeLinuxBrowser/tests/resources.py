"""Linux /proc samples for the helper tree, excluding the test consumer.

100% CPU means one logical core. RSS sums can count shared pages more than once.
This intentionally measures CPU rendering/decoding, not GPU utilization.
"""
import os
from pathlib import Path
import time


class Resources:
    def __init__(self, pid):
        self.pid = pid
        self.started = time.monotonic()
        self.previous = {}
        self.ticks = 0
        self.peak_rss = 0
        self.max_processes = 0
        self.sample()
        self.ticks = 0

    def sample(self):
        pending, visited, rss = [self.pid], set(), 0
        while pending:
            pid = pending.pop()
            if pid in visited:
                continue
            visited.add(pid)
            try:
                stat = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
                identity = (pid, stat[19])  # PID plus start time prevents PID reuse.
                ticks = int(stat[11]) + int(stat[12])
                self.ticks += max(0, ticks - self.previous.get(identity, 0))
                self.previous[identity] = ticks
                rss += int(stat[21]) * os.sysconf("SC_PAGE_SIZE")
                pending.extend(int(p) for p in Path(f"/proc/{pid}/task/{pid}/children").read_text().split())
            except (FileNotFoundError, ProcessLookupError):
                pass
        self.peak_rss = max(self.peak_rss, rss)
        self.max_processes = max(self.max_processes, len(visited))

    def result(self):
        elapsed = time.monotonic() - self.started
        return dict(helper_tree_cpu_percent_one_core_100=self.ticks / os.sysconf("SC_CLK_TCK") / elapsed * 100,
                    helper_tree_peak_summed_rss_mib=self.peak_rss / 1024**2,
                    helper_tree_max_processes=self.max_processes,
                    resource_notes="/proc samples; consumer excluded; shared RSS may be counted multiple times; no GPU measurement")
