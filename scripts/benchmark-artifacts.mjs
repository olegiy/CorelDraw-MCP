import assert from 'node:assert/strict';
import {crc32,inflateSync} from 'node:zlib';
// The benchmark requests 8-bit RGB PNG. Canonical RGBA excludes metadata from comparisons.
export function decodePng(bytes) {
  assert(bytes.subarray(0,8).equals(Buffer.from([137,80,78,71,13,10,26,10])));
  let width,height,channels,interlace;const idat=[];
  for(let offset=8;offset<bytes.length;) {
    assert(offset+12<=bytes.length);const length=bytes.readUInt32BE(offset),end=offset+8+length;assert(end+4<=bytes.length);
    const kind=bytes.toString('ascii',offset+4,offset+8),data=bytes.subarray(offset+8,end);assert.equal(crc32(bytes.subarray(offset+4,end)),bytes.readUInt32BE(end));
    if(kind==='IHDR') {assert.equal(length,13);width=data.readUInt32BE(0);height=data.readUInt32BE(4);assert(width>0&&height>0&&width*height<=40000000);assert.equal(data[8],8);assert([2,6].includes(data[9]));channels=data[9]===2?3:4;assert.equal(data[10],0);assert.equal(data[11],0);interlace=data[12];assert([0,1].includes(interlace));}
    if(kind==='IDAT')idat.push(data);offset=end+4;if(kind==='IEND')break;
  }
  assert(channels&&idat.length);
  const passes=(interlace?[[0,0,8,8],[4,0,8,8],[0,4,4,8],[2,0,4,4],[0,2,2,4],[1,0,2,2],[0,1,1,2]]:[[0,0,1,1]])
    .map(([sx,sy,dx,dy])=>({sx,sy,dx,dy,w:Math.max(0,Math.ceil((width-sx)/dx)),h:Math.max(0,Math.ceil((height-sy)/dy))})).filter(p=>p.w&&p.h);
  const expected=passes.reduce((sum,p)=>sum+(p.w*channels+1)*p.h,0),raw=inflateSync(Buffer.concat(idat),{maxOutputLength:expected});assert.equal(raw.length,expected);
  const decoded=Buffer.alloc(width*height*channels),paeth=(a,b,c)=>{const p=a+b-c,da=Math.abs(p-a),db=Math.abs(p-b),dc=Math.abs(p-c);return da<=db&&da<=dc?a:db<=dc?b:c;};let cursor=0;
  for(const pass of passes) {
    const stride=pass.w*channels;let previous=Buffer.alloc(stride),current=Buffer.alloc(stride);
    for(let y=0;y<pass.h;y++) {
      const filter=raw[cursor++];assert(filter<=4);
      for(let x=0;x<stride;x++) {
        const left=x>=channels?current[x-channels]:0,up=previous[x],corner=x>=channels?previous[x-channels]:0;
        current[x]=(raw[cursor++]+[0,left,up,Math.floor((left+up)/2),paeth(left,up,corner)][filter])&255;
      }
      for(let x=0;x<pass.w;x++)current.copy(decoded,((pass.sy+y*pass.dy)*width+pass.sx+x*pass.dx)*channels,x*channels,(x+1)*channels);
      [previous,current]=[current,previous];
    }
  }
  const rgba=Buffer.alloc(width*height*4);let painted=0;
  for(let i=0;i<width*height;i++) {const at=i*channels;rgba[i*4]=decoded[at];rgba[i*4+1]=decoded[at+1];rgba[i*4+2]=decoded[at+2];rgba[i*4+3]=channels===4?decoded[at+3]:255;if(rgba[i*4+3]&&Math.min(decoded[at],decoded[at+1],decoded[at+2])<250)painted++;}
  return {width,height,rgba,painted};
}
