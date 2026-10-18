'use strict';
// Only the native PlayTime notice emitted by its verified timer callback.
// Other types and other callers keep the original notification path.
let listener=null,installed=false;
function bytes(p,n){return Array.from(new Uint8Array(p.readByteArray(n)),v=>v.toString(16).padStart(2,'0')).join('');}
function filter(target,caller){
  // Native gate preserves all registers/flags and forwards the original ABI.
  const code=Memory.alloc(Process.pageSize),counter=Memory.alloc(4);
  let original;
  function write(){Memory.patchCode(code,128,p=>{
    const data=[0x50,0x9c,0x48,0xb8];
    function address(v){const b=new Uint8Array(8);for(let i=0;i<8;i++)b[i]=v.shr(i*8).and(255).toUInt32();data.push(...b);}
    address(caller);data.push(0x48,0x39,0x44,0x24,0x10,0x75,0);const first=data.length-1;
    data.push(0x41,0x80,0x39,119,0x75,0);const second=data.length-1;
    data.push(0x48,0xb8);address(counter);data.push(0xff,0x00,0x9d,0x58,0xc3);
    const pass=data.length;data[first]=pass-first-1;data[second]=pass-second-1;
    data.push(0x9d,0x58,0xff,0x25,0,0,0,0);address(original||target);p.writeByteArray(data);
  });}
  write();Memory.protect(code,Process.pageSize,'r-x');
  original=Interceptor.replaceFast(target,code);write();Interceptor.flush();
  return {count(){return counter.readU32();},detach(){Interceptor.revert(target);Interceptor.flush();},code,counter};
}
rpc.exports={
  settle(){
    if(!installed){
      const game=Process.getModuleByName('KartDrift-Win64-Shipping.exe');
      const target=game.base.add(0x1bf45c0),call=game.base.add(0x1bc9bca);
      if(bytes(target,16)!=='48895c24185556574154415541564157'||bytes(call,5)!=='e8f1a90200')
        throw new Error('PlayTime notice binary version mismatch');
      listener=filter(target,call.add(5));installed=true;
      send({kind:'playtime_notice_filter_ready',type:119});
    }
    return {installed,blocked:listener?listener.count():0};
  },
  status(){return {installed,blocked:listener?listener.count():0};},
  probe(){
    // Machine-code fixture, never attached to a game: target increments one
    // counter per notice type. Two callers distinguish the timer from others.
    const counts=Memory.alloc(256*4),target=Memory.alloc(Process.pageSize);
    Memory.patchCode(target,64,p=>{
      const w=new X86Writer(p,{pc:target});w.putMovRegAddress('rax',counts);
      w.putBytes([0x41,0x0f,0xb6,0x09,0xff,0x04,0x88]);w.putRet();w.flush();
    });
    Memory.protect(target,Process.pageSize,'r-x');
    function caller(){
      const code=Memory.alloc(Process.pageSize);let after;
      Memory.patchCode(code,64,p=>{const w=new X86Writer(p,{pc:code});w.putSubRegImm('rsp',40);w.putMovRegAddress('rax',target);w.putCallReg('rax');after=code.add(w.offset);w.putAddRegImm('rsp',40);w.putRet();w.flush();});
      Memory.protect(code,Process.pageSize,'r-x');
      return {code,after,invoke:new NativeFunction(code,'void',['pointer','pointer','pointer','pointer'])};
    }
    const timer=caller(),other=caller(),hook=filter(target,timer.after),value=Memory.alloc(1);
    try{
      for(let t=0;t<256;t++){value.writeU8(t);timer.invoke(ptr(0),ptr(0),ptr(0),value);}
      const timerCount=counts.add(119*4).readU32();value.writeU8(119);other.invoke(ptr(0),ptr(0),ptr(0),value);
      let unchanged=0;for(let t=0;t<256;t++)if(t!==119&&counts.add(t*4).readU32()===1)unchanged++;
      return {timerCount,otherCallerCount:counts.add(119*4).readU32(),unchangedTypes:unchanged,blocked:hook.count()};
    }finally{hook.detach();}
  }
};

