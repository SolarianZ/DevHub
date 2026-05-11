#!/usr/bin/env python3
import sys
import time

duration_seconds = 2.0
if len(sys.argv) > 1:
    duration_seconds = max(0.0, float(sys.argv[1]))

# 保持短暂存活，给测试窗口，避免长时间占用资源。
start = time.time()
while time.time() - start < duration_seconds:
    time.sleep(0.05)
