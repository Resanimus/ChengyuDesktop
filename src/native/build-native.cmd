@echo off
setlocal
where cl.exe >nul 2>&1 || exit /b 1
where fxc.exe >nul 2>&1 || exit /b 1
pushd "%~dp0"
fxc /nologo /T vs_5_0 /E main /O3 /Vn g_Quad /Fh shaders\quad.h shaders\quad.hlsl
if errorlevel 1 goto failed
fxc /nologo /T ps_5_0 /E main /O3 /Vn g_Liquid /Fh shaders\liquid.h shaders\glass.hlsl
if errorlevel 1 goto failed
fxc /nologo /T ps_5_0 /E main /O3 /D FROSTED=1 /Vn g_Frosted /Fh shaders\frosted.h shaders\glass.hlsl
if errorlevel 1 goto failed
cl /nologo /LD /O2 /EHsc /std:c++17 /MT GlassCapture.cpp GlassRenderer.cpp /link /OUT:ChengyuGlass.dll d3d11.lib dxgi.lib user32.lib
if errorlevel 1 goto failed
popd
exit /b 0
:failed
popd
exit /b 1
