# Win11 虚拟显示驱动阶段

Windows 真正的“扩展屏”必须创建一个 Windows 显示目标。推荐基于 Microsoft Windows Driver Samples 的 IndirectDisplay / IddCx 示例改造。

官方样例：
- Windows-driver-samples/video/IndirectDisplay
- IddSampleDriver + IddSampleApp

本项目第二阶段要改的点：

1. 只枚举 1 台虚拟显示器。
2. 显示器名称改为 `WinPad Display`。
3. 默认首选模式改成 1920x1440@60。
4. 保留 1920x1280、1600x1200、1280x960 等回退模式。
5. 在 SwapChainProcessor 取得每一帧的 D3D11 texture。
6. 不在驱动进程里直接做复杂 UI；将帧交给用户态 streamer/service。
7. 正式发布前必须处理驱动签名、安装器、升级/卸载和异常恢复。

## 推荐初始模式表

```cpp
static const struct SampleMonitorMode kWinPadModes[] = {
    { 1920, 1440, 60 },
    { 1920, 1280, 60 },
    { 1600, 1200, 60 },
    { 1280,  960, 60 },
    { 1280,  800, 60 },
};
```

注意：最终模式应由 iPad 客户端上报逻辑分辨率/刷新率，再动态更新，而不是永久写死。
