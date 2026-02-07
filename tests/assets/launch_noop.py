#!/usr/bin/env python3
import time

# 保持短暂存活，给测试窗口
# 避免长时间占用资源
start = time.time()
while time.time() - start < 2.0:
    time.sleep(0.05)
