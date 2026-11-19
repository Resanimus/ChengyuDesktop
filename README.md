# 澄屿桌面 ChengyuDesktop

Windows 桌面管理工具，提供 Dock、启动台、灵动岛、桌面组件和本地壁纸库。

- Dock 支持固定应用、运行窗口分组、悬停放大和拖出取消固定。桌面组件支持吸附对齐与位置锁定。
- 收纳盒支持多个实例、内部排序、图标尺寸调整与拖出移除。组件还包括时钟、待办、性能和实时频谱。
- 本地壁纸库支持扫描多个目录。视频和网页可直接播放，部分 Wallpaper Engine `scene.pkg` 项目可通过 WebView2 和 WebWallGL 独立试播。
- 多窗口合计遮住主屏工作区约 95% 时，播放器暂停。持续遮挡 12 秒可释放解码器或 WebView2。部分遮挡时，场景按覆盖面积降低目标帧率。

## 使用

面向 Windows 10 2004 及更新版本、Windows 11。到 [Releases](https://github.com/Resanimus/ChengyuDesktop/releases) 下载 x64 便携包，解压后运行 `ChengyuDock.exe`。网页和场景壁纸需要 Microsoft Edge WebView2 Runtime。场景脚本和系统音频响应暂未接入，复杂特效的表现随壁纸而异。

公开版采用独立的 D3D11 玻璃着色器、Windows 系统字体与「午夜微光」预览图。任务栏按需显示和开机启动在控制台里开启。

## 从源码构建

安装 .NET 10 SDK、Visual Studio C++ Build Tools 及带 `fxc.exe` 的 Windows SDK。使用 **x64 Native Tools Command Prompt** 在仓库目录执行：

```bat
powershell -NoProfile -ExecutionPolicy Bypass -File setup-assets.ps1
call src\native\build-native.cmd
dotnet publish src\ChengyuDock.csproj -c Release -r win-x64 --self-contained -p:DebugType=none -o dist
```

使用独立数据目录运行 `--self-test` 或 `--integration-test --exit-after-test`。桌面与壁纸测试需要已解锁的 Windows 会话。

第三方许可见 [licenses](licenses) 及 [WebWallGL 说明](src/Assets/ScenePlayer/Third-party-notices.txt)。
