# TaskbarGuard 1.4.1 hosted by 澄屿

ChengyuTaskbarGuard.ahk 保留原版 10px 触底、Win 键唤出、原生收起等待和 145ms 淡入逻辑。
托管改动：隐藏独立托盘、使用独立窗口标记，通过父进程句柄和停止事件在关闭开关或宿主退出后恢复任务栏。
--fixture 只操作指定验证窗口，不操作系统任务栏。Ctrl+Alt+F12 仍可紧急退出组件。
保持 Windows 的“自动隐藏任务栏”开启。关闭它时引擎暂停遮挡；重新开启后继续。

runtime/AutoHotkey64.exe 是未经修改的 AutoHotkey v2.0.29 x64。完整 GPL v2 许可证见 runtime/license.txt。
对应版本源代码：https://github.com/AutoHotkey/AutoHotkey/tree/v2.0.29
