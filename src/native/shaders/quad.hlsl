struct PixelInput { float4 position:SV_POSITION; float2 uv:TEXCOORD0; };
PixelInput main(uint id:SV_VertexID) {
    PixelInput p; p.uv=float2((id<<1)&2,id&2);
    p.position=float4(p.uv.x*2-1,1-p.uv.y*2,0,1); return p;
}
