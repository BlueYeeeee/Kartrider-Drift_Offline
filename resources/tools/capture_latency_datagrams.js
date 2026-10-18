
const m=Process.getModuleByName('ws2_32.dll');
function capture(p,n,a,kind,context) {
 if(n<=0 || n>256 || a.isNull() || a.readU16()!==2)return;
 const port=a.add(2).readU8()*256+a.add(3).readU8();if(port!==51474)return;
 const stack=Thread.backtrace(context,Backtracer.ACCURATE).slice(0,10).map(p=>{
  const m=Process.findModuleByAddress(p);return m?m.name+'+'+p.sub(m.base):'unknown';
 });
 send({kind,port,ip:[4,5,6,7].map(i=>a.add(i).readU8()).join('.'),length:n,
  hex:Array.from(new Uint8Array(p.readByteArray(n))).map(x=>x.toString(16).padStart(2,'0')).join(''),stack});
}
Interceptor.attach(m.getExportByName('sendto'),{onEnter(a){try{capture(a[1],a[2].toInt32(),a[4],'send',this.context);}catch(e){send({error:String(e)});}}});
Interceptor.attach(m.getExportByName('recvfrom'),{onEnter(a){this.p=a[1];this.a=a[4];},onLeave(r){try{capture(this.p,r.toInt32(),this.a,'receive',this.context);}catch(e){send({error:String(e)});}}});
