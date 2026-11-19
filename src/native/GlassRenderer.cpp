#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <map>
#include <mutex>
#include <memory>
#include <cstring>
#include <algorithm>
#include "shaders/quad.h"
#include "shaders/liquid.h"
#include "shaders/frosted.h"
using Microsoft::WRL::ComPtr;
namespace chengyu {
struct Device {
    ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDXGIOutput1> output; ComPtr<IDXGIOutputDuplication> duplication;
    ComPtr<ID3D11VertexShader> vertex; ComPtr<ID3D11PixelShader> liquid,frosted;
    ComPtr<ID3D11SamplerState> sampler; ComPtr<ID3D11Buffer> constants;
    ComPtr<ID3D11Texture2D> scene,readback; ComPtr<ID3D11ShaderResourceView> sceneView;
    DXGI_OUTPUT_DESC desc{}; UINT width=0,height=0; bool hasFrame=false;
    ULONGLONG lastPoll=0,sequence=0;
    HRESULT init(HMONITOR monitor) {
        ComPtr<IDXGIFactory1> factory; HRESULT hr=CreateDXGIFactory1(IID_PPV_ARGS(&factory)); if(FAILED(hr))return hr;
        ComPtr<IDXGIAdapter1> adapter;
        for(UINT a=0;!output;++a) {
            ComPtr<IDXGIAdapter1> candidate;if(factory->EnumAdapters1(a,&candidate)==DXGI_ERROR_NOT_FOUND)break;
            for(UINT o=0;;++o){ComPtr<IDXGIOutput> item;if(candidate->EnumOutputs(o,&item)==DXGI_ERROR_NOT_FOUND)break;
                DXGI_OUTPUT_DESC d{};item->GetDesc(&d);if(d.Monitor==monitor){adapter=candidate;item.As(&output);desc=d;break;}}
        }
        if(!output)return E_NOINTERFACE;
        if(desc.Rotation!=DXGI_MODE_ROTATION_IDENTITY&&desc.Rotation!=DXGI_MODE_ROTATION_UNSPECIFIED)return DXGI_ERROR_UNSUPPORTED;
        hr=D3D11CreateDevice(adapter.Get(),D3D_DRIVER_TYPE_UNKNOWN,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context);if(FAILED(hr))return hr;
        if(FAILED(hr=device->CreateVertexShader(g_Quad,sizeof(g_Quad),nullptr,&vertex)))return hr;
        if(FAILED(hr=device->CreatePixelShader(g_Liquid,sizeof(g_Liquid),nullptr,&liquid)))return hr;
        if(FAILED(hr=device->CreatePixelShader(g_Frosted,sizeof(g_Frosted),nullptr,&frosted)))return hr;
        D3D11_SAMPLER_DESC s{};s.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;s.AddressU=s.AddressV=s.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;s.MaxLOD=D3D11_FLOAT32_MAX;
        if(FAILED(hr=device->CreateSamplerState(&s,&sampler)))return hr;
        D3D11_BUFFER_DESC b{};b.ByteWidth=144;b.Usage=D3D11_USAGE_DYNAMIC;b.BindFlags=D3D11_BIND_CONSTANT_BUFFER;b.CPUAccessFlags=D3D11_CPU_ACCESS_WRITE;
        if(FAILED(hr=device->CreateBuffer(&b,nullptr,&constants)))return hr;
        width=desc.DesktopCoordinates.right-desc.DesktopCoordinates.left;height=desc.DesktopCoordinates.bottom-desc.DesktopCoordinates.top;
        D3D11_TEXTURE2D_DESC t{};t.Width=width;t.Height=height;t.MipLevels=0;t.ArraySize=1;t.Format=DXGI_FORMAT_B8G8R8A8_UNORM;t.SampleDesc.Count=1;t.Usage=D3D11_USAGE_DEFAULT;
        t.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_RENDER_TARGET;t.MiscFlags=D3D11_RESOURCE_MISC_GENERATE_MIPS;
        if(FAILED(hr=device->CreateTexture2D(&t,nullptr,&scene)))return hr;
        return device->CreateShaderResourceView(scene.Get(),nullptr,&sceneView);
    }
    HRESULT acquire(bool force=false) {
        ULONGLONG tick=GetTickCount64();if(!force&&hasFrame&&tick-lastPoll<15)return S_OK;lastPoll=tick;
        if(!duplication){HRESULT h=output->DuplicateOutput(device.Get(),&duplication);if(FAILED(h))return h;hasFrame=false;}
        DXGI_OUTDUPL_FRAME_INFO info{};ComPtr<IDXGIResource> resource;
        HRESULT hr=duplication->AcquireNextFrame(force?32:0,&info,&resource);
        if(hr==DXGI_ERROR_ACCESS_LOST){duplication.Reset();hasFrame=false;return hr;}
        if(hr==DXGI_ERROR_WAIT_TIMEOUT)return hasFrame?S_OK:S_FALSE;
        if(FAILED(hr))return hr;
        if(info.LastPresentTime.QuadPart||!hasFrame){ComPtr<ID3D11Texture2D> image;
            if(SUCCEEDED(resource.As(&image))){context->CopySubresourceRegion(scene.Get(),0,0,0,0,image.Get(),0,nullptr);context->GenerateMips(sceneView.Get());hasFrame=true;++sequence;}}
        duplication->ReleaseFrame();return hasFrame?S_OK:S_FALSE;
    }
};
struct Surface {
    Device* owner=nullptr;UINT width=0,height=0,sourceW=0,sourceH=0;ULONGLONG sourceRevision=0,lastSequence=0; bool valid=false; int lastShader=0;float lastConstants[36]{};
    ComPtr<ID3D11Texture2D> target,staging,source;ComPtr<ID3D11RenderTargetView> targetView;ComPtr<ID3D11ShaderResourceView> sourceView;
    HRESULT resize(Device* d,UINT w,UINT h) {
        if(owner==d&&target&&width==w&&height==h)return S_OK;
        target.Reset();targetView.Reset();staging.Reset();source.Reset();sourceView.Reset();sourceRevision=0;sourceW=sourceH=0;owner=d;width=w;height=h;valid=false;
        D3D11_TEXTURE2D_DESC t{};t.Width=w;t.Height=h;t.MipLevels=1;t.ArraySize=1;t.Format=DXGI_FORMAT_B8G8R8A8_UNORM;t.SampleDesc.Count=1;t.Usage=D3D11_USAGE_DEFAULT;t.BindFlags=D3D11_BIND_RENDER_TARGET;
        HRESULT hr=d->device->CreateTexture2D(&t,nullptr,&target);if(FAILED(hr))return hr;
        if(FAILED(hr=d->device->CreateRenderTargetView(target.Get(),nullptr,&targetView)))return hr;
        t.Usage=D3D11_USAGE_STAGING;t.BindFlags=0;t.CPUAccessFlags=D3D11_CPU_ACCESS_READ;return d->device->CreateTexture2D(&t,nullptr,&staging);
    }
    HRESULT input(Device* d,const unsigned char* pixels,UINT w,UINT h,ULONGLONG revision) {
        HRESULT hr=S_OK;
        if(!source||sourceW!=w||sourceH!=h){source.Reset();sourceView.Reset();sourceRevision=0;sourceW=w;sourceH=h;
            D3D11_TEXTURE2D_DESC t{};t.Width=w;t.Height=h;t.MipLevels=0;t.ArraySize=1;t.Format=DXGI_FORMAT_B8G8R8A8_UNORM;t.SampleDesc.Count=1;t.Usage=D3D11_USAGE_DEFAULT;
            t.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_RENDER_TARGET;t.MiscFlags=D3D11_RESOURCE_MISC_GENERATE_MIPS;
            if(FAILED(hr=d->device->CreateTexture2D(&t,nullptr,&source)))return hr;
            if(FAILED(hr=d->device->CreateShaderResourceView(source.Get(),nullptr,&sourceView)))return hr;}
        if(sourceRevision!=revision){d->context->UpdateSubresource(source.Get(),0,nullptr,pixels,w*4,0);d->context->GenerateMips(sourceView.Get());sourceRevision=revision;}
        return hr;
    }
};
std::mutex mutex;std::map<HMONITOR,std::unique_ptr<Device>> devices;std::map<unsigned long long,std::unique_ptr<Surface>> surfaces;unsigned long long nextId=1;
Device* get(HMONITOR monitor,HRESULT& result){auto found=devices.find(monitor);if(found!=devices.end())return found->second.get();auto d=std::make_unique<Device>();result=d->init(monitor);if(FAILED(result))return nullptr;auto p=d.get();devices.emplace(monitor,std::move(d));return p;}
}
extern "C" __declspec(dllexport) unsigned long long __cdecl GlassSurfaceCreate(){std::lock_guard<std::mutex> lock(chengyu::mutex);auto id=chengyu::nextId++;chengyu::surfaces.emplace(id,std::make_unique<chengyu::Surface>());return id;}
extern "C" __declspec(dllexport) void __cdecl GlassSurfaceDestroy(unsigned long long id){std::lock_guard<std::mutex> lock(chengyu::mutex);chengyu::surfaces.erase(id);if(chengyu::surfaces.empty())chengyu::devices.clear();}
extern "C" __declspec(dllexport) int __cdecl GlassSurfaceRender(unsigned long long id,const float* values,int w,int h,int shader,unsigned char* destination,int capacity,
 const unsigned char* input,int inputW,int inputH,int inputX,int inputY,unsigned long long revision){
 if(!values||!destination||w<1||h<1||w>8192||h>8192||static_cast<long long>(w)*h*4>capacity)return E_INVALIDARG;
 std::lock_guard<std::mutex> lock(chengyu::mutex);auto found=chengyu::surfaces.find(id);if(found==chengyu::surfaces.end())return S_FALSE;
 RECT r{static_cast<LONG>(values[8]),static_cast<LONG>(values[9]),static_cast<LONG>(values[8]+values[10]),static_cast<LONG>(values[9]+values[11])};
 HRESULT hr=S_OK;auto d=chengyu::get(MonitorFromRect(&r,MONITOR_DEFAULTTONEAREST),hr);if(!d)return hr;auto s=found->second.get();
 if(FAILED(hr=s->resize(d,w,h)))return hr;float cb[36];std::memcpy(cb,values,144);ID3D11ShaderResourceView* view=nullptr;
 if(input&&inputW>0&&inputH>0){if(FAILED(hr=s->input(d,input,inputW,inputH,revision)))return hr;view=s->sourceView.Get();cb[4]=(float)inputX;cb[5]=(float)inputY;cb[6]=(float)inputW;cb[7]=(float)inputH;}
 else {if((hr=d->acquire())!=S_OK)return hr;view=d->sceneView.Get();cb[4]=(float)d->desc.DesktopCoordinates.left;cb[5]=(float)d->desc.DesktopCoordinates.top;cb[6]=(float)d->width;cb[7]=(float)d->height;}
 cb[16]=cb[4];cb[17]=cb[5];cb[18]=cb[4]+cb[6]-1;cb[19]=cb[5]+cb[7]-1;
 ULONGLONG seq=input?revision:d->sequence;
 if(s->valid&&s->lastSequence==seq&&s->lastShader==shader&&!std::memcmp(s->lastConstants,cb,144))return S_FALSE;
 s->lastSequence=seq;s->lastShader=shader;std::memcpy(s->lastConstants,cb,144);
 auto c=d->context.Get();D3D11_MAPPED_SUBRESOURCE mapped{};if(FAILED(hr=c->Map(d->constants.Get(),0,D3D11_MAP_WRITE_DISCARD,0,&mapped)))return hr;
 std::memcpy(mapped.pData,cb,144);c->Unmap(d->constants.Get(),0);c->ClearState();
 ID3D11RenderTargetView* target=s->targetView.Get();c->OMSetRenderTargets(1,&target,nullptr);float clear[4]{};c->ClearRenderTargetView(target,clear);
 D3D11_VIEWPORT viewport{0,0,(float)w,(float)h,0,1};c->RSSetViewports(1,&viewport);c->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
 c->VSSetShader(d->vertex.Get(),nullptr,0);c->PSSetShader(shader==2?d->frosted.Get():d->liquid.Get(),nullptr,0);
 ID3D11Buffer* buffer=d->constants.Get();c->PSSetConstantBuffers(0,1,&buffer);ID3D11SamplerState* sampler=d->sampler.Get();c->PSSetSamplers(0,1,&sampler);c->PSSetShaderResources(0,1,&view);c->Draw(3,0);
 ID3D11ShaderResourceView* nullView=nullptr;c->PSSetShaderResources(0,1,&nullView);c->OMSetRenderTargets(0,nullptr,nullptr);c->CopyResource(s->staging.Get(),s->target.Get());
 if(FAILED(hr=c->Map(s->staging.Get(),0,D3D11_MAP_READ,0,&mapped)))return hr;
 bool changed=!s->valid;
 for(int row=0;row<h;++row){auto src=(unsigned char*)mapped.pData+row*mapped.RowPitch;auto dst=destination+row*w*4;if(!changed&&std::memcmp(dst,src,w*4))changed=true;std::memcpy(dst,src,w*4);}
 s->valid=true;c->Unmap(s->staging.Get(),0);return changed?S_OK:S_FALSE;
}
extern "C" __declspec(dllexport) int __cdecl GlassCapturePixels(int left,int top,int w,int h,unsigned char* destination,int capacity){
 if(!destination||w<1||h<1||static_cast<long long>(w)*h*4>capacity)return E_INVALIDARG;
 std::lock_guard<std::mutex> lock(chengyu::mutex);RECT r{left,top,left+w,top+h};HRESULT hr=S_OK;auto d=chengyu::get(MonitorFromRect(&r,MONITOR_DEFAULTTONEAREST),hr);if(!d)return hr;
 if((hr=d->acquire(true))!=S_OK)return hr;int x=left-d->desc.DesktopCoordinates.left,y=top-d->desc.DesktopCoordinates.top;if(x<0||y<0||x+w>(int)d->width||y+h>(int)d->height)return E_INVALIDARG;
 D3D11_TEXTURE2D_DESC t{};t.Width=w;t.Height=h;t.MipLevels=1;t.ArraySize=1;t.Format=DXGI_FORMAT_B8G8R8A8_UNORM;t.SampleDesc.Count=1;t.Usage=D3D11_USAGE_STAGING;t.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
 ComPtr<ID3D11Texture2D> stage;if(FAILED(hr=d->device->CreateTexture2D(&t,nullptr,&stage)))return hr;D3D11_BOX box{(UINT)x,(UINT)y,0,(UINT)(x+w),(UINT)(y+h),1};
 d->context->CopySubresourceRegion(stage.Get(),0,0,0,0,d->scene.Get(),0,&box);D3D11_MAPPED_SUBRESOURCE mapped{};if(FAILED(hr=d->context->Map(stage.Get(),0,D3D11_MAP_READ,0,&mapped)))return hr;
 for(int row=0;row<h;++row)std::memcpy(destination+row*w*4,(unsigned char*)mapped.pData+row*mapped.RowPitch,w*4);d->context->Unmap(stage.Get(),0);return S_OK;
}
