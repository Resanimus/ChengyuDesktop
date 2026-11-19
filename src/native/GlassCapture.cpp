#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <new>
#include <cstring>
using Microsoft::WRL::ComPtr;

struct Capture {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDXGIOutputDuplication> duplication;
    ComPtr<ID3D11Texture2D> snapshot, staging;
    DXGI_OUTPUT_DESC output{};
    UINT width = 0, height = 0, stageW = 0, stageH = 0;
    bool hasFrame = false;
    unsigned long long sequence = 0;
};

extern "C" __declspec(dllexport) void* __cdecl GlassCaptureCreate(HMONITOR monitor) {
    auto c = new (std::nothrow) Capture(); if (!c) return nullptr;
    ComPtr<IDXGIFactory1> factory;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) { delete c; return nullptr; }
    ComPtr<IDXGIAdapter1> chosen;
    ComPtr<IDXGIOutput> selected;
    for (UINT a = 0; !selected; ++a) {
        ComPtr<IDXGIAdapter1> adapter;
        if (factory->EnumAdapters1(a, &adapter) == DXGI_ERROR_NOT_FOUND) break;
        for (UINT o = 0;; ++o) {
            ComPtr<IDXGIOutput> output;
            if (adapter->EnumOutputs(o, &output) == DXGI_ERROR_NOT_FOUND) break;
            DXGI_OUTPUT_DESC desc{}; if (FAILED(output->GetDesc(&desc))) continue;
            if (desc.Monitor == monitor) { chosen = adapter; selected = output; c->output = desc; break; }
        }
    }
    if (!selected) { delete c; return nullptr; }
    D3D_FEATURE_LEVEL level;
    HRESULT hr = D3D11CreateDevice(chosen.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr, 0, D3D11_SDK_VERSION, &c->device, &level, &c->context);
    ComPtr<IDXGIOutput1> out1;
    if (FAILED(hr) || FAILED(selected.As(&out1)) || FAILED(out1->DuplicateOutput(c->device.Get(), &c->duplication))) { delete c; return nullptr; }
    auto r = c->output.DesktopCoordinates; c->width = r.right - r.left; c->height = r.bottom - r.top;
    // Portrait/rotated outputs need a separate coordinate transform; fall back on those hosts.
    if (c->output.Rotation != DXGI_MODE_ROTATION_IDENTITY && c->output.Rotation != DXGI_MODE_ROTATION_UNSPECIFIED) { delete c; return nullptr; }
    D3D11_TEXTURE2D_DESC desc{}; desc.Width = c->width; desc.Height = c->height; desc.MipLevels = 1; desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1; desc.Usage = D3D11_USAGE_DEFAULT;
    if (FAILED(c->device->CreateTexture2D(&desc, nullptr, &c->snapshot))) { delete c; return nullptr; }
    return c;
}

extern "C" __declspec(dllexport) int __cdecl GlassCaptureRead(void* handle, int left, int top, int width, int height,
    unsigned char* destination, int capacity, unsigned long long* sequence) {
    auto c = static_cast<Capture*>(handle);
    if (!c || !destination || width < 1 || height < 1 || width > 8192 || height > 2048 ||
        static_cast<long long>(width) * height * 4 > capacity) return E_INVALIDARG;
    int x = left - c->output.DesktopCoordinates.left, y = top - c->output.DesktopCoordinates.top;
    if (x < 0 || y < 0 || x + width > static_cast<int>(c->width) || y + height > static_cast<int>(c->height)) return E_INVALIDARG;
    DXGI_OUTDUPL_FRAME_INFO info{}; ComPtr<IDXGIResource> resource;
    HRESULT hr = c->duplication->AcquireNextFrame(0, &info, &resource);
    if (SUCCEEDED(hr)) {
        if (info.LastPresentTime.QuadPart || !c->hasFrame) {
            ComPtr<ID3D11Texture2D> texture;
            if (SUCCEEDED(resource.As(&texture))) { c->context->CopyResource(c->snapshot.Get(), texture.Get()); c->hasFrame = true; ++c->sequence; }
        }
        c->duplication->ReleaseFrame();
    } else if (hr != DXGI_ERROR_WAIT_TIMEOUT) return hr;
    if (!c->hasFrame) return 1;
    if (c->stageW != static_cast<UINT>(width) || c->stageH != static_cast<UINT>(height)) {
        c->staging.Reset();
        D3D11_TEXTURE2D_DESC desc{}; desc.Width = width; desc.Height = height; desc.MipLevels = 1; desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1; desc.Usage = D3D11_USAGE_STAGING; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        hr = c->device->CreateTexture2D(&desc, nullptr, &c->staging); if (FAILED(hr)) return hr;
        c->stageW = width; c->stageH = height;
    }
    D3D11_BOX box{ static_cast<UINT>(x), static_cast<UINT>(y), 0, static_cast<UINT>(x + width), static_cast<UINT>(y + height), 1 };
    c->context->CopySubresourceRegion(c->staging.Get(), 0, 0, 0, 0, c->snapshot.Get(), 0, &box);
    D3D11_MAPPED_SUBRESOURCE mapped{}; hr = c->context->Map(c->staging.Get(), 0, D3D11_MAP_READ, 0, &mapped); if (FAILED(hr)) return hr;
    for (int row = 0; row < height; ++row) std::memcpy(destination + row * width * 4, static_cast<unsigned char*>(mapped.pData) + row * mapped.RowPitch, width * 4);
    c->context->Unmap(c->staging.Get(), 0);
    if (sequence) *sequence = c->sequence;
    return 0;
}

extern "C" __declspec(dllexport) void __cdecl GlassCaptureDestroy(void* handle) { delete static_cast<Capture*>(handle); }
